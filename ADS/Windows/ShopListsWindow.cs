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
    private readonly Dictionary<uint, int> quantityEdits = [];
    private IReadOnlyList<ShopListPreviewRow> previewRows = [];
    private string previewState = string.Empty;
    private string newPresetName = string.Empty;
    private string renamePresetName = string.Empty;
    private int newItemId;
    private int newItemQuantity = 1;
    private string status = string.Empty;
    private DateTime? observedBatchCompletionUtc;

    public ShopListsWindow(Plugin plugin)
        : base("ADS Shop Lists###ADSShopLists")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(850f, 560f),
            MaximumSize = new Vector2(1800f, 1300f),
        };
        Size = new Vector2(1180f, 760f);
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
        ImGui.TextUnformatted("Purchase batch");
        ImGui.TextWrapped(batch.Running ? batch.StatusMessage : plugin.UtilityAutomationService.StatusMessage);
        if (batch.TotalRows > 0)
            ImGui.TextDisabled($"Completed rows: {batch.CompletedRows}/{batch.TotalRows}");

        if (ImGui.Button("Refresh XA Database + preview"))
        {
            service.RefreshOwnership(out status);
            RefreshPreview();
        }
        ImGui.SameLine();
        if (ImGui.Button("Refresh preview"))
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
        var presetIndex = Math.Max(
            0,
            Array.FindIndex(
                presetNames,
                name => string.Equals(name, store.ActivePresetName, StringComparison.OrdinalIgnoreCase)));
        if (ImGui.Combo("Active preset", ref presetIndex, presetNames, presetNames.Length))
        {
            SetStatus(service.SelectPreset(presetNames[presetIndex], out var error), error);
            RefreshPreview();
        }

        ImGui.SetNextItemWidth(260f);
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
                RefreshPreview();
            }
        }

        ImGui.SetNextItemWidth(260f);
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
            RefreshPreview();
        }
        ImGui.EndDisabled();

        ImGui.SetNextItemWidth(150f);
        ImGui.InputInt("Item ID", ref newItemId);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        ImGui.InputInt("Desired total", ref newItemQuantity);
        ImGui.SameLine();
        if (ImGui.Button("Add / update item"))
        {
            if (newItemId <= 0)
            {
                status = "Item ID must be a positive decimal integer.";
            }
            else
            {
                var succeeded = service.SetItem((uint)newItemId, newItemQuantity, out var error);
                SetStatus(succeeded, error);
                if (succeeded)
                {
                    newItemId = 0;
                    newItemQuantity = 1;
                    RefreshPreview();
                }
            }
        }
    }

    private void DrawImportControls()
    {
        ImGui.TextUnformatted("Replace active preset from clipboard");
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
                ImGuiTableFlags.Borders
                | ImGuiTableFlags.RowBg
                | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.Resizable
                | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, -1f)))
        {
            return;
        }

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn("Desired", ImGuiTableColumnFlags.WidthFixed, 105f);
        ImGui.TableSetupColumn("Inventory", ImGuiTableColumnFlags.WidthFixed, 75f);
        ImGui.TableSetupColumn("Retainers / locations", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Selected vendor", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Status / route error", ImGuiTableColumnFlags.WidthStretch, 1.8f);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableHeadersRow();

        foreach (var row in previewRows)
        {
            ImGui.PushID(unchecked((int)row.ItemId));
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextWrapped(row.ItemName);
            ImGui.TextDisabled(row.ItemId.ToString(CultureInfo.InvariantCulture));

            ImGui.TableSetColumnIndex(1);
            if (!quantityEdits.TryGetValue(row.ItemId, out var editedQuantity))
                editedQuantity = row.DesiredQuantity;
            ImGui.SetNextItemWidth(65f);
            if (ImGui.InputInt("##Quantity", ref editedQuantity))
                quantityEdits[row.ItemId] = editedQuantity;
            ImGui.SameLine();
            if (ImGui.SmallButton("Save"))
            {
                var succeeded = plugin.ShopListService.SetItem(row.ItemId, editedQuantity, out var error);
                SetStatus(succeeded, error);
                if (succeeded)
                    RefreshPreview();
            }

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(row.LiveInventoryQuantity.ToString("N0", CultureInfo.InvariantCulture));

            ImGui.TableSetColumnIndex(3);
            DrawRetainerLocations(row);

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(row.PurchaseQuantity.ToString("N0", CultureInfo.InvariantCulture));

            ImGui.TableSetColumnIndex(5);
            if (row.SelectedOffer == null)
            {
                ImGui.TextDisabled("None");
            }
            else
            {
                ImGui.TextWrapped(row.SelectedOffer.ShopName);
                ImGui.TextDisabled($"{row.SelectedOffer.ShopKind} {row.SelectedOffer.ShopId}");
                ImGui.TextDisabled($"{row.SelectedOffer.NpcName} - {row.SelectedOffer.TerritoryName}");
            }

            ImGui.TableSetColumnIndex(6);
            if (!string.IsNullOrWhiteSpace(row.FailureCode))
                ImGui.TextWrapped($"{row.FailureCode}: {row.StatusMessage}");
            else
                ImGui.TextWrapped(row.StatusMessage);

            ImGui.TableSetColumnIndex(7);
            if (ImGui.SmallButton("Remove"))
            {
                SetStatus(plugin.ShopListService.RemoveItem(row.ItemId, out var error), error);
                RefreshPreview();
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
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
            return plugin.ShopListService.OwnershipStatus;

        var blocked = previewRows.FirstOrDefault(row => row.PurchaseQuantity > 0
                                                        && (row.SelectedOffer == null || !string.IsNullOrEmpty(row.FailureCode)));
        return blocked == null
            ? string.Empty
            : $"Resolve the preview error for {blocked.ItemName} before running: {blocked.StatusMessage}";
    }

    private void RefreshPreviewIfChanged()
    {
        var store = plugin.ShopListService.PresetStore;
        var state = store.ActivePresetName + "|" + string.Join(
            ';',
            store.ActivePreset.Items.Select(item => $"{item.ItemId}:{item.Quantity}"));
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
            RefreshPreview();
    }

    private void RefreshPreview()
    {
        try
        {
            previewRows = plugin.ShopListService.BuildPreviewRows();
            quantityEdits.Clear();
            foreach (var row in previewRows)
                quantityEdits[row.ItemId] = row.DesiredQuantity;

            var store = plugin.ShopListService.PresetStore;
            previewState = store.ActivePresetName + "|" + string.Join(
                ';',
                store.ActivePreset.Items.Select(item => $"{item.ItemId}:{item.Quantity}"));
        }
        catch (Exception ex)
        {
            previewRows = [];
            status = $"Shop-list preview failed safely: {ex.Message}";
            Plugin.Log.Warning(ex, "[ADS][ShopLists] Preview failed.");
        }
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
}
