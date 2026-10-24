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
    private Task<(ShopCatalogSearchResponse? Result, string? Error)>? catalogSearch;
    private Guid catalogSearchPresetId;
    private ShopCurrencyIdentity? catalogSearchCurrency;
    private string previewState = string.Empty;
    private string newPresetName = string.Empty;
    private string renamePresetName = string.Empty;
    private int relicExampleIndex;
    private int gearExpansionIndex;
    private int gearJobIndex;
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
    private Guid selectedRowId;
    private ShopCatalogSearchRow? selectedCatalogRow;

    public ShopListsWindow(Plugin plugin)
        : base("ADS Shop Lists###ADSShopLists")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(920f, 620f),
            MaximumSize = new Vector2(1800f, 1300f),
        };
        Size = plugin.Configuration.UiCompact ? new Vector2(980f, 620f) : new Vector2(1280f, 820f);
    }

    public void Dispose()
    {
        catalogSearch = null; // The read-only worker cannot apply results after unload.
    }

    public override void Draw()
    {
        DrawWindowChrome();
        FinalizePendingWindowPlacement();
        RefreshPreviewIfChanged();
        RefreshAfterBatchCompletion();
        var originalIdRoot = ImGui.GetID("");
        using var controls = MaterialControls.Push(MaterialControlContext.Toolbar);
        using (plugin.Fonts.Push(UiFontRole.BodyStrong)) MaterialText.Text(Ui.T("Shop Lists"));
        DrawPresetSelector();
        DrawBatchControls();
        EnsurePresetSettings();
        if (plugin.ShopListService.PresetStore.ActivePreset.Mode == ShopListMode.FillOrderOverMultipleRuns)
        {
            newTriggerBelow = newRefillToAtLeast;
            newRepeatable = false;
        }
        if (ImGui.BeginTabBar("ADSShopListsTabs", ImGuiTabBarFlags.FittingPolicyScroll))
        {
            try
            {
            if (WindowLayout.BeginTabItem(Ui.L("Order & preview"), ImGuiTabItemFlags.NoPushId))
            {
                ImGuiP.PushOverrideID(originalIdRoot);
                try
                {
                    if (MaterialText.CollapsingHeader(Ui.L("Preset management"))) DrawPresetSettings();
                    DrawAddRow();
                    DrawPreview();
                    DrawBatchDetails();
                }
                finally { ImGui.PopID(); ImGui.EndTabItem(); }
            }
            if (WindowLayout.BeginTabItem(Ui.L("Catalog"), ImGuiTabItemFlags.NoPushId))
            {
                ImGuiP.PushOverrideID(originalIdRoot);
                try { DrawCatalogSearch(); }
                finally { ImGui.PopID(); ImGui.EndTabItem(); }
            }
            if (WindowLayout.BeginTabItem(Ui.L("Presets & import"), ImGuiTabItemFlags.NoPushId))
            {
                ImGuiP.PushOverrideID(originalIdRoot);
                try
                {
                    DrawPresetControls();
                    DrawImportControls();
                }
                finally { ImGui.PopID(); ImGui.EndTabItem(); }
            }
            }
            finally { ImGui.EndTabBar(); }
        }
        // Complete read-only work even when its view is hidden, after preset/import actions.
        CompleteCatalogSearch(plugin.ShopListService.PresetStore.ActivePreset);

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
        MaterialText.TextWrapped(Ui.Display(batch.Running ? batch.StatusMessage : plugin.UtilityAutomationService.StatusMessage));

        if (WindowLayout.Button(Ui.L("Test preset (preview only)")))
        {
            var test = service.PreviewActivePreset();
            ApplyPreviewRows(test.Rows);
            testedPreviewState = previewState;
            testedDisposition = test.Disposition;
            testedMessage = test.Message;
            status = $"Test {test.Disposition}: {test.Message} No travel or purchase was started.";
        }
        WindowLayout.SameLineIfFits(WindowLayout.ButtonMinimum(Ui.L("Refresh local preview")));
        if (WindowLayout.Button(Ui.L("Refresh local preview")))
            RefreshPreview();

        var runBlocker = GetRunBlocker();
        WindowLayout.SameLineIfFits(WindowLayout.ButtonMinimum(Ui.L("Run Shop List")));
        ImGui.BeginDisabled(!string.IsNullOrEmpty(runBlocker));
        if (WindowLayout.Button(Ui.L("Run Shop List")))
        {
            plugin.StartShopListBatch(out status);
            RefreshPreview();
        }
        ImGui.EndDisabled();

        WindowLayout.SameLineIfFits(WindowLayout.ButtonMinimum(Ui.L("Cancel Shop List")));
        ImGui.BeginDisabled(!batch.Running);
        if (WindowLayout.Button(Ui.L("Cancel Shop List")))
            plugin.CancelUtility();
        ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(runBlocker))
            MaterialText.TextDisabled(Ui.Display(runBlocker));

        if (service.IsStandaloneOrderComplete)
        {
            if (WindowLayout.Button(Ui.L("Start new order")))
            {
                SetStatus(service.StartNewOrder(out var error), error);
                RefreshPreview();
            }
        }
    }

    private void DrawBatchDetails()
    {
        if (!MaterialText.CollapsingHeader(Ui.L("Preset test and purchase"))) return;
        var service = plugin.ShopListService;
        var batch = plugin.UtilityAutomationService.ShopListBatchStatus;
        if (batch.TotalRows > 0)
            MaterialText.TextDisabled(Ui.T("Completed rows: {0}/{1} | operation: {2}", batch.CompletedRows, batch.TotalRows, batch.OperationId));
        MaterialText.TextWrapped(Ui.T("Targeted refill uses current ownership thresholds. Spend until currency/capacity keeps spending under its repeat rules. Fill order over multiple runs credits initial ownership once, then verified purchases; consuming or moving items keeps that progress."));

        MaterialText.TextWrapped(Ui.Display(service.OwnershipStatus));
        if (service.OwnershipRefreshedAtUtc != DateTime.MinValue)
        {
            MaterialText.TextDisabled(
                Ui.T("Ownership response read: {0}", service.OwnershipRefreshedAtUtc.ToString("u", CultureInfo.InvariantCulture)));
        }
        DrawWarnings("XA Database warnings", service.OwnershipWarnings);
    }

    private void DrawPresetSelector()
    {
        var service = plugin.ShopListService;
        var store = service.PresetStore;
        var presetNames = store.Presets.Select(preset => preset.Name).ToArray();
        var presetIndex = Math.Max(0, Array.FindIndex(
            presetNames,
            name => string.Equals(name, store.ActivePresetName, StringComparison.OrdinalIgnoreCase)));
        ImGui.SetNextItemWidth(WindowLayout.ComboMinimum(presetNames));
        if (WindowLayout.Combo(WindowLayout.InputLabel("Active preset", WindowLayout.ComboMinimum(presetNames)), ref presetIndex, presetNames, presetNames.Length))
        {
            SetStatus(service.SelectPreset(presetNames[presetIndex], out var error), error);
            settingsPresetId = Guid.Empty;
            RefreshPreview();
        }
    }

    private void DrawPresetControls()
    {
        var service = plugin.ShopListService;
        var store = service.PresetStore;
        MaterialSettings.Section(Ui.T("Presets"));
        MaterialText.TextWrapped(Ui.Display(store.LastStatus));
        ImGui.BeginDisabled(plugin.UtilityAutomationService.IsRunning);
        if (WindowLayout.Button(Ui.L("Reload saved lists")))
        {
            store.Reload();
            settingsPresetId = Guid.Empty;
            RefreshPreview();
        }
        ImGui.EndDisabled();
        WindowLayout.SameLineIfFits(WindowLayout.ButtonMinimum(Ui.L("Copy preset ID")));
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

        DrawGearExamples(service);

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
    }

    private void DrawPresetSettings()
    {
        var service = plugin.ShopListService;
        EnsurePresetSettings();
        var modes = new[] { "Targeted refill", "Spend until currency/capacity", "Fill order over multiple runs" };
        var modeLabels = modes.Select(Ui.Display).ToArray();
        ImGui.SetNextItemWidth(WindowLayout.ComboMinimum(modeLabels));
        WindowLayout.Combo(WindowLayout.InputLabel("Purchase type", WindowLayout.ComboMinimum(modeLabels)), ref presetModeIndex, modeLabels, modes.Length);

        var currencyKinds = Enum.GetValues<ShopCurrencyKind>();
        var currencyNames = currencyKinds.Select(ShopOfferSelector.CurrencyKindName).Select(Ui.Display).ToArray();
        ImGui.SetNextItemWidth(WindowLayout.ComboMinimum(currencyNames));
        if (WindowLayout.Combo(WindowLayout.InputLabel("Exact currency kind", WindowLayout.ComboMinimum(currencyNames)), ref currencyKindIndex, currencyNames, currencyNames.Length))
        {
            currencyItemId = currencyKinds[currencyKindIndex] switch
            {
                ShopCurrencyKind.Gil => 1,
                ShopCurrencyKind.FreeCompanyCredit => 0,
                _ => currencyItemId,
            };
        }
        WindowLayout.SameLineIfFits(120 * MaterialTheme.Metrics.Scale + MaterialText.Measure(Ui.T("Currency item ID")).X + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.SetNextItemWidth(120f);
        WindowLayout.InputInt(WindowLayout.InputLabel("Currency item ID"), ref currencyItemId);
        WindowLayout.SameLineIfFits(130 * MaterialTheme.Metrics.Scale + MaterialText.Measure(Ui.T("Trigger at >=")).X + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.SetNextItemWidth(130f);
        WindowLayout.InputInt(WindowLayout.InputLabel("Trigger at >="), ref currencyThreshold);
        WindowLayout.SameLineIfFits(WindowLayout.ButtonMinimum(Ui.L("Save preset settings")));
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

    }

    private void DrawAddRow()
    {
        var store = plugin.ShopListService.PresetStore;
        MaterialSettings.Section(Ui.T("Add row"));
        ImGui.SetNextItemWidth(120f);
        WindowLayout.InputInt(WindowLayout.InputLabel("Item ID"), ref newItemId);
        WindowLayout.SameLineIfFits(95 * MaterialTheme.Metrics.Scale + MaterialText.Measure(Ui.T(store.ActivePreset.Mode == ShopListMode.FillOrderOverMultipleRuns ? "Target quantity" : "If owned <")).X + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.SetNextItemWidth(95f);
        if (store.ActivePreset.Mode == ShopListMode.FillOrderOverMultipleRuns)
        {
            WindowLayout.InputInt(WindowLayout.InputLabel("Target quantity"), ref newRefillToAtLeast);
            newTriggerBelow = newRefillToAtLeast;
            newRepeatable = false;
        }
        else
        {
            WindowLayout.InputInt(WindowLayout.InputLabel("If owned <"), ref newTriggerBelow);
            WindowLayout.SameLineIfFits(95 * MaterialTheme.Metrics.Scale + MaterialText.Measure(Ui.T("Refill to >=")).X + ImGui.GetStyle().ItemInnerSpacing.X);
            ImGui.SetNextItemWidth(95f);
            WindowLayout.InputInt(WindowLayout.InputLabel("Refill to >="), ref newRefillToAtLeast);
            WindowLayout.SameLineIfFits(MaterialText.Measure(Ui.T("Repeatable")).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
            WindowLayout.Checkbox("Repeatable", ref newRepeatable);
        }
        var scopes = new[] { "Inventory only", "Inventory + XA Database retainers" };
        WindowLayout.SameLineIfFits(WindowLayout.ComboMinimum(scopes.Select(Ui.Display)) + MaterialText.Measure(Ui.T("Item ownership")).X + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.SetNextItemWidth(WindowLayout.ComboMinimum(scopes.Select(Ui.Display)));
        WindowLayout.Combo(WindowLayout.InputLabel("Item ownership", WindowLayout.ComboMinimum(scopes.Select(Ui.Display))), ref newOwnershipScopeIndex,scopes.Select(Ui.Display).ToArray(), scopes.Length);
        WindowLayout.SameLineIfFits(WindowLayout.ButtonMinimum(Ui.L("Add / update item")));
        if (WindowLayout.Button(Ui.L("Add / update item")))
            AddOrUpdateItem((uint)Math.Max(0, newItemId));
    }

    private void DrawGearExamples(ShopListService service)
    {
        var expansions = ShopListExamples.GearExpansions;
        using var controls = MaterialControls.Push(MaterialControlContext.Toolbar);
        ImGui.PushID("poetics-gear-examples");
        try
        {
            var expansionNames = expansions.Select(expansion => Ui.Display(expansion.Name)).ToArray();
            ImGui.SetNextItemWidth(340f * MaterialTheme.Metrics.Scale);
            WindowLayout.Combo(WindowLayout.InputLabel("Expansion", WindowLayout.ComboMinimum(expansionNames)),
                ref gearExpansionIndex, expansionNames, expansionNames.Length);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(WindowLayout.ComboMinimum(ShopListExamples.GearJobs));
            WindowLayout.Combo(WindowLayout.InputLabel("Job", WindowLayout.ComboMinimum(ShopListExamples.GearJobs)),
                ref gearJobIndex, ShopListExamples.GearJobs, ShopListExamples.GearJobs.Length);
            ImGui.SameLine();
            if (WindowLayout.Button(Ui.L("Add gear example")))
            {
                var succeeded = service.AddExamplePreset(expansions[gearExpansionIndex].Jobs[gearJobIndex], out var error);
                SetStatus(succeeded, error);
                if (succeeded)
                {
                    renamePresetName = string.Empty;
                    settingsPresetId = Guid.Empty;
                    RefreshPreview();
                    status = "Example added as an editable preset. Review targets, then Test preset before Run Shop List.";
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                try { MaterialText.Text(Ui.T(ShopListExamples.GearDescription), ImGui.GetFontSize() * 32); }
                finally { ImGui.EndTooltip(); }
            }
        }
        finally { ImGui.PopID(); }
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

    private void BeginCatalogSearch(Guid presetId, ShopCurrencyIdentity? currency)
    {
        if (catalogSearch != null) return;
        var query = catalogQuery;
        var service = plugin.UtilityAutomationService;
        catalogSearchPresetId = presetId;
        catalogSearchCurrency = currency;
        catalogRows = [];
        status = "Searching vendor catalog...";
        catalogSearch = Task.Run(() =>
        {
            try { return (Result: (ShopCatalogSearchResponse?)service.SearchShopCatalog(query, currency, 100), Error: (string?)null); }
            catch (Exception ex) { return (Result: (ShopCatalogSearchResponse?)null, Error: (string?)ex.Message); }
        });
    }

    private void CompleteCatalogSearch(ShopListPreset active)
    {
        if (catalogSearch?.IsCompleted != true) return;
        var response = catalogSearch.GetAwaiter().GetResult();
        catalogSearch = null;
        if (active.PresetId != catalogSearchPresetId || (catalogSearchCurrency.HasValue && active.Currency != catalogSearchCurrency.Value))
        {
            status = "Catalog search cancelled: the preset or currency changed.";
            return;
        }
        if (response.Error != null) { status = $"Catalog search failed: {response.Error}"; return; }
        catalogRows = response.Result!.Rows;
        status = catalogSearchCurrency.HasValue
            ? $"Catalog returned {catalogRows.Count} exact-currency offer(s)."
            : $"Catalog returned {catalogRows.Count} offer(s); use a row's exact currency identity below.";
    }

    private void DrawCatalogSearch()
    {
        var active = plugin.ShopListService.PresetStore.ActivePreset;
        CompleteCatalogSearch(active);
        MaterialText.Text(Ui.T("Deterministic vendor catalog"));
        ImGui.BeginDisabled(catalogSearch != null);
        ImGui.SetNextItemWidth(300f);
        WindowLayout.InputText(WindowLayout.InputLabel("Item / vendor / NPC / territory / currency", WindowLayout.TextMinimum(120)), ref catalogQuery, 120);
        WindowLayout.SameLineIfFits(WindowLayout.ButtonMinimum(Ui.L("Search exact preset currency")));
        if (WindowLayout.Button(Ui.L("Search exact preset currency")))
        {
            BeginCatalogSearch(active.PresetId, active.Currency);
        }
        WindowLayout.SameLineIfFits(WindowLayout.ButtonMinimum(Ui.L("Discover currencies")));
        if (WindowLayout.Button(Ui.L("Discover currencies")))
        {
            BeginCatalogSearch(active.PresetId, null);
        }
        ImGui.EndDisabled();

        if (catalogRows.Count == 0)
        {
            MaterialText.TextDisabled(Ui.T(catalogSearch != null ? "Searching vendor catalog..."
                : "Search results appear here. Catalog search and Test never travel or purchase."));
            return;
        }

        using var catalogControls = MaterialTable.PushControls();
        using var catalogRowsStyle = MaterialTable.PushTightRows();
        var catalogRowHeight = Math.Max(ImGui.GetFrameHeight(), (AdsPresentation.Compact ? 29 : 34) * MaterialTheme.Metrics.Scale);
        var catalogHeight = Math.Min(ImGui.GetFrameHeight() + catalogRows.Count * catalogRowHeight + 4 * MaterialTheme.Metrics.Scale,
            Math.Max(catalogRowHeight * 2, Math.Min(300f * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().Y * .55f)));
        var actionWidth = new[] { "Use currency", "Add item", "Copy XYZ" }.Sum(label => WindowLayout.ButtonMinimum(Ui.L(label)))
            + ImGui.GetStyle().ItemSpacing.X * 2;
        var innerWidth = Math.Max(ImGui.GetContentRegionAvail().X, actionWidth + 700 * MaterialTheme.Metrics.Scale);
        if (innerWidth > ImGui.GetContentRegionAvail().X) catalogHeight += ImGui.GetStyle().ScrollbarSize;
        if (!ImGui.BeginTable(
                "ADSShopCatalogSearch",
                5,
                ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.ScrollX | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, catalogHeight), innerWidth))
        {
            return;
        }
        ImGui.TableSetupColumn(Ui.L("Currency"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(Ui.L("Item / bundle"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(Ui.L("Vendor / NPC"), ImGuiTableColumnFlags.WidthStretch, 1.7f);
        ImGui.TableSetupColumn(Ui.L("Territory / XYZ"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn(Ui.L("Actions"), ImGuiTableColumnFlags.WidthFixed, actionWidth);
        ImGui.TableSetupScrollFreeze(0, 1);
        WindowLayout.TableHeadersRow();

        foreach (var row in catalogRows)
        {
            ImGui.PushID(HashCode.Combine(row.ItemId, row.ShopId, row.ShopRow, row.NpcId, row.TerritoryId));
            ImGui.TableNextRow(ImGuiTableRowFlags.None, catalogRowHeight);
            ImGui.TableSetColumnIndex(0);
            MaterialText.Text(Ui.T("{0}: {1}", Ui.ItemName(row.CurrencyItemId, row.CurrencyName), row.CurrencyCostPerTransaction));
            ImGui.TableSetColumnIndex(1);
            if (MaterialText.Selectable(Ui.ItemName(row.ItemId, row.ItemName) + "###SelectShopCatalogRow", Equals(selectedCatalogRow, row)))
                selectedCatalogRow = row;
            if (ImGui.IsItemHovered()) MaterialText.SetTooltip(Ui.T("{0} | receive {1}", row.ItemId, row.ReceiveCount));
            ImGui.TableSetColumnIndex(2);
            MaterialText.Text(Ui.NpcName(row.NpcId, row.NpcName));
            ImGui.TableSetColumnIndex(3);
            MaterialText.Text(Ui.TerritoryName(row.TerritoryId, row.TerritoryName));
            ImGui.TableSetColumnIndex(4);
            if (WindowLayout.SmallButton(Ui.L("Use currency")))
                UseCatalogCurrency(row);
            ImGui.SameLine();
            if (WindowLayout.SmallButton(Ui.L("Add item")))
                AddOrUpdateItem(row.ItemId);
            ImGui.SameLine();
            if (WindowLayout.SmallButton(Ui.L("Copy XYZ")))
                ImGui.SetClipboardText(row.CopyableXyz);
            ImGui.PopID();
        }
        ImGui.EndTable();
        var selected = catalogRows.FirstOrDefault(row => Equals(row, selectedCatalogRow)) ?? catalogRows.FirstOrDefault();
        if (selected is not null)
        {
            MaterialSettings.Section(Ui.T("Details"));
            MaterialText.Text(Ui.ItemName(selected.ItemId, selected.ItemName));
            MaterialText.TextDisabled(Ui.T("{0} | receive {1}", selected.ItemId, selected.ReceiveCount));
            MaterialText.Text(Ui.T("{0}:{1}", selected.CurrencyKind, selected.CurrencyItemId));
            MaterialText.TextWrapped(Ui.ShopName(selected.ShopKind, selected.ShopId, selected.ShopName));
            MaterialText.TextDisabled(Ui.T("shop {0} row {1}", selected.ShopId, selected.ShopRow));
            MaterialText.TextWrapped(Ui.NpcName(selected.NpcId, selected.NpcName));
            MaterialText.TextDisabled(selected.NpcId.ToString(CultureInfo.InvariantCulture));
            MaterialText.TextWrapped(Ui.TerritoryName(selected.TerritoryId, selected.TerritoryName));
            MaterialText.TextDisabled(Ui.T("{0} | {1}", selected.TerritoryId, selected.CopyableXyz));
        }
    }

    private void DrawPreview()
    {
        MaterialSettings.Section(Ui.T("Preview"));
        if (previewRows.Count == 0)
        {
            MaterialText.TextDisabled(Ui.T("The active preset has no items."));
            return;
        }

        var fillOrder = plugin.ShopListService.PresetStore.ActivePreset.Mode == ShopListMode.FillOrderOverMultipleRuns;
        var numberWidth = Math.Max(WindowLayout.NumberMinimum(false),
            new[] { fillOrder ? "Target" : "If owned <", fillOrder ? "Completed" : "Refill to >=" }
                .Max(label => MaterialText.Measure(Ui.T(label)).X + ImGui.GetStyle().CellPadding.X * 2));
        var completedWidth = numberWidth;
        if (fillOrder)
        {
            // This getter only copies existing progress; measuring it does not save or run an order.
            var progress = plugin.ShopListService.PresetStore.GetOrderProgress(
                plugin.UtilityAutomationService.ShopCharacterId, plugin.ShopListService.PresetStore.ActivePresetId);
            completedWidth = Math.Max(completedWidth, previewRows.Max(row =>
                MaterialText.Measure(Ui.T("Credited: {0} / {1}", progress.GetValueOrDefault(row.RowId), row.RefillToAtLeast)).X)
                + ImGui.GetStyle().CellPadding.X * 2);
        }
        var actionWidth = new[] { "Details", "Save row", "Remove" }.Sum(label => WindowLayout.ButtonMinimum(Ui.L(label)))
            + ImGui.GetStyle().ItemSpacing.X * 2;
        using var tableControls = MaterialTable.PushControls();
        using var rowsStyle = MaterialTable.PushTightRows();
        var rowHeight = Math.Max(ImGui.GetFrameHeight(), (AdsPresentation.Compact ? 29 : 34) * MaterialTheme.Metrics.Scale);
        var previewHeight = Math.Min(ImGui.GetFrameHeight() + previewRows.Count * rowHeight + 4 * MaterialTheme.Metrics.Scale,
            Math.Max(rowHeight * 2, Math.Min(300f * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().Y * .48f)));
        var innerWidth = Math.Max(ImGui.GetContentRegionAvail().X, actionWidth + numberWidth + completedWidth + 540 * MaterialTheme.Metrics.Scale);
        if (innerWidth > ImGui.GetContentRegionAvail().X) previewHeight += ImGui.GetStyle().ScrollbarSize;
        if (!ImGui.BeginTable(
                "ADSShopListPreview",
                8,
                ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX
                | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, previewHeight), innerWidth))
        {
            return;
        }
        ImGui.TableSetupColumn(Ui.L("Item / row"), ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn(Ui.L(fillOrder ? "Target" : "If owned <"), ImGuiTableColumnFlags.WidthFixed, numberWidth);
        ImGui.TableSetupColumn(Ui.L(fillOrder ? "Completed" : "Refill to >="), ImGuiTableColumnFlags.WidthFixed, completedWidth);
        ImGui.TableSetupColumn(Ui.L("Owned"), ImGuiTableColumnFlags.WidthFixed, Math.Max(95f * MaterialTheme.Metrics.Scale, MaterialText.Measure(Ui.T("Owned")).X));
        ImGui.TableSetupColumn(Ui.L("Would buy"), ImGuiTableColumnFlags.WidthFixed, Math.Max(70f * MaterialTheme.Metrics.Scale, MaterialText.Measure(Ui.T("Would buy")).X));
        ImGui.TableSetupColumn(Ui.L("Selected vendor"), ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn(Ui.L("Outcome / status"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn(Ui.L("Action"), ImGuiTableColumnFlags.WidthFixed, actionWidth);
        ImGui.TableSetupScrollFreeze(0, 1);
        WindowLayout.TableHeadersRow();

        foreach (var row in previewRows)
        {
            ImGui.PushID(row.RowId.GetHashCode());
            if (!rowEdits.TryGetValue(row.RowId, out var edit))
                edit = rowEdits[row.RowId] = RowEdit.From(row);

            ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
            ImGui.TableSetColumnIndex(0);
            if (MaterialText.Selectable(Ui.ItemName(row.ItemId, row.ItemName) + "###SelectShopOrderRow", selectedRowId == row.RowId))
                selectedRowId = row.RowId;
            if (ImGui.IsItemHovered()) MaterialText.SetTooltip(row.ItemId.ToString(CultureInfo.InvariantCulture));

            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1f);
            if (fillOrder)
            {
                WindowLayout.InputInt("###Target", ref edit.RefillToAtLeast);
                edit.TriggerBelow = edit.RefillToAtLeast;
                edit.Repeatable = false;
                var credited = plugin.ShopListService.PresetStore.GetOrderProgress(
                    plugin.UtilityAutomationService.ShopCharacterId, plugin.ShopListService.PresetStore.ActivePresetId);
                ImGui.TableSetColumnIndex(2);
                MaterialText.Text(Ui.T("Credited: {0} / {1}", credited.GetValueOrDefault(row.RowId), row.RefillToAtLeast));
            }
            else
            {
                WindowLayout.InputInt("###If owned <##Trigger", ref edit.TriggerBelow);
                ImGui.TableSetColumnIndex(2);
                ImGui.SetNextItemWidth(-1f);
                WindowLayout.InputInt("###Refill to >=##Refill", ref edit.RefillToAtLeast);
            }
            ImGui.TableSetColumnIndex(3);
            MaterialText.Text(row.OwnedQuantity.ToString("N0", Ui.Culture));
            if (ImGui.IsItemHovered()) MaterialText.SetTooltip(Ui.T("Inv {0:N0}", row.LiveInventoryQuantity));

            ImGui.TableSetColumnIndex(4);
            MaterialText.Text(row.PurchaseQuantity.ToString("N0", Ui.Culture));

            ImGui.TableSetColumnIndex(5);
            if (row.SelectedOffer == null)
            {
                using var disabledText = new MaterialStyleScope();
                disabledText.Color(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                MaterialText.Text(Ui.T("None needed / available"));
            }
            else
            {
                MaterialText.Text(Ui.NpcName(row.SelectedOffer.NpcId, row.SelectedOffer.NpcName));
            }

            ImGui.TableSetColumnIndex(6);
            MaterialText.Text(Ui.Display(row.Outcome));
            if (ImGui.IsItemHovered()) MaterialText.SetTooltip(Ui.Display(row.StatusMessage));

            ImGui.TableSetColumnIndex(7);
            if (WindowLayout.SmallButton(Ui.L("Details"))) selectedRowId = row.RowId;
            ImGui.SameLine();
            if (WindowLayout.SmallButton(Ui.L("Save row"))) SaveRow(row, edit);
            ImGui.SameLine();
            if (WindowLayout.SmallButton(Ui.L("Remove")))
            {
                SetStatus(plugin.ShopListService.RemoveItem(row.RowId, out var error), error);
                RefreshPreview();
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
        var selected = previewRows.FirstOrDefault(row => row.RowId == selectedRowId) ?? previewRows.FirstOrDefault();
        if (selected is not null) DrawSelectedItem(selected, fillOrder);
    }

    private void SaveRow(ShopListPreviewRow row, RowEdit edit)
    {
        var succeeded = plugin.ShopListService.UpdateItem(row.RowId, edit.TriggerBelow, edit.RefillToAtLeast,
            edit.Repeatable, (ShopListOwnershipScope)Math.Clamp(edit.OwnershipScopeIndex, 0, 1), out var error);
        SetStatus(succeeded, error);
        if (succeeded) RefreshPreview();
    }

    private void DrawSelectedItem(ShopListPreviewRow row, bool fillOrder)
    {
        selectedRowId = row.RowId;
        if (!rowEdits.TryGetValue(row.RowId, out var edit)) return;
        MaterialSettings.Section(Ui.T("Details"));
        MaterialText.Text(Ui.ItemName(row.ItemId, row.ItemName));
        ImGui.SameLine();
        MaterialText.TextDisabled(row.ItemId.ToString(CultureInfo.InvariantCulture));
        // Retain the row table's original scope for the moved secondary editors.
        var tableRoot = ImGui.GetID("ADSShopListPreview");
        ImGuiP.PushOverrideID(tableRoot);
        ImGui.PushID(row.RowId.GetHashCode());
        try
        {
            if (!fillOrder) WindowLayout.Checkbox("Repeatable", ref edit.Repeatable);
            var scopeNames = new[] { "Inventory", "Inventory + retainers" }.Select(Ui.Display).ToArray();
            WindowLayout.SameLineIfFits(WindowLayout.ComboMinimum(scopeNames));
            ImGui.SetNextItemWidth(WindowLayout.ComboMinimum(scopeNames));
            WindowLayout.Combo("##Scope", ref edit.OwnershipScopeIndex, scopeNames, scopeNames.Length);
            WindowLayout.SameLineIfFits(WindowLayout.ButtonMinimum(Ui.L("Copy row ID")));
            if (WindowLayout.SmallButton(Ui.L("Copy row ID"))) ImGui.SetClipboardText(row.RowId.ToString("D"));
            // The table already draws Save with its retained ID; this second view has its own ID.
            ImGui.PushID("ADSSelectedItem");
            try
            {
                WindowLayout.SameLineIfFits(WindowLayout.ButtonMinimum(Ui.L("Save row")));
                if (WindowLayout.SmallButton(Ui.L("Save row"))) SaveRow(row, edit);
            }
            finally { ImGui.PopID(); }
        }
        finally { ImGui.PopID(); ImGui.PopID(); }
        MaterialText.TextDisabled(Ui.T("Inv {0:N0}", row.LiveInventoryQuantity));
        MaterialText.Text(Ui.T("Selected vendor"));
        if (row.SelectedOffer is { } offer)
        {
            MaterialText.TextWrapped(Ui.ShopName(offer.ShopKind, offer.ShopId, offer.ShopName));
            MaterialText.TextDisabled(Ui.T("{0} {1}", offer.ShopKind, offer.ShopId));
            MaterialText.TextWrapped(Ui.T("{0} - {1}", Ui.NpcName(offer.NpcId, offer.NpcName), Ui.TerritoryName(offer.TerritoryId, offer.TerritoryName)));
        }
        else MaterialText.TextDisabled(Ui.T("None needed / available"));
        MaterialText.Text(Ui.T("Retainer locations"));
        DrawRetainerLocations(row);
        MaterialText.TextWrapped(Ui.T("{0}: {1}", Ui.Display(row.Outcome), Ui.Display(row.StatusMessage)));
        if (!string.IsNullOrWhiteSpace(row.FailureCode)) MaterialText.TextDisabled(row.FailureCode);
        if (plugin.ShopListService.PresetStore.ActivePreset.Mode == ShopListMode.TargetedRefill)
            MaterialText.TextWrapped(Ui.T("Set both 'If owned <' and 'Refill to >=' to 130 to target 130 owned items. With single-item vendor bundles, owning 10 means buying 120. /ads shop <itemID> 130 requests 130 additional items."));
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
