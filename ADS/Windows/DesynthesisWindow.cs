using System.Numerics;
using ADS.Models;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace ADS.Windows;

public sealed class DesynthesisWindow : PositionedWindow, IDisposable
{
    private static readonly DesynthRunMode[] PolicyRunModes =
    [
        DesynthRunMode.Configured,
        DesynthRunMode.All,
        DesynthRunMode.Whitelist,
        DesynthRunMode.LastDuty,
        DesynthRunMode.Skillups,
    ];

    private static readonly (DesynthInventoryScope Scope, string Label)[] ScopeOptions =
    [
        (DesynthInventoryScope.InventoryOnly, "Inventory only"),
        (DesynthInventoryScope.InventoryAndArmourySkipGearsets, "Inventory + armoury, skip gearsets"),
        (DesynthInventoryScope.InventoryAndArmoury, "Inventory + armoury"),
    ];

    private readonly Plugin plugin;
    private string newPresetName = string.Empty;
    private string newPresetDescription = string.Empty;
    private string renamePresetName = string.Empty;
    private string itemText = string.Empty;
    private string itemSearch = string.Empty;
    private uint selectedItemId;
    private string selectedItemName = string.Empty;
    private string importExportText = string.Empty;
    private string status = string.Empty;
    private bool itemsLoaded;
    private readonly List<(uint Id, string Name)> desynthableItems = [];

    public DesynthesisWindow(Plugin plugin)
        : base("ADS Desynthesis###ADSDesynthesis")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560f, 500f),
            MaximumSize = new Vector2(1400f, 1200f),
        };
        Size = new Vector2(720f, 760f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();
        EnsureDesynthItemsLoaded();
        DrawRunControls();
        ImGui.Separator();
        DrawPolicy();
        ImGui.Separator();
        DrawPresets();
        ImGui.Separator();
        DrawLedger();
        if (!string.IsNullOrWhiteSpace(status))
            ImGui.TextWrapped(status);
    }

    private void DrawRunControls()
    {
        ImGui.TextUnformatted($"Status: {plugin.UtilityAutomationService.StatusMessage}");
        ImGui.TextDisabled($"Mode: {plugin.UtilityAutomationService.ActiveDesynthModeName}; scope {plugin.UtilityAutomationService.ActiveDesynthScopeName}; eligible {plugin.UtilityAutomationService.DesynthEligibleCount}; completed {plugin.UtilityAutomationService.DesynthCompletedCount}");

        if (ImGui.Button("Run inventory only"))
            plugin.StartDesynth("inventory-only");
        ImGui.SameLine();
        if (ImGui.Button("Run everywhere, skip gearsets"))
            plugin.StartDesynth("everywhere-skip-gearsets");
        ImGui.SameLine();
        if (ImGui.Button("Run everywhere"))
            plugin.StartDesynth("everywhere");
        ImGui.SameLine();
        if (ImGui.Button("Stop utility"))
            plugin.CancelUtility();

        foreach (var mode in PolicyRunModes)
        {
            if (ImGui.Button($"Run {ModeLabel(mode)}"))
                plugin.StartDesynth(ModeLabel(mode));
            if (mode != DesynthRunMode.Skillups)
                ImGui.SameLine();
        }
    }

    private void DrawPolicy()
    {
        ImGui.TextUnformatted("Configured policy");
        var sources = Enum.GetNames<DesynthSource>();
        var sourceIndex = Array.IndexOf(sources, plugin.Configuration.DesynthSource.ToString());
        if (ImGui.Combo("Run filter source", ref sourceIndex, sources, sources.Length))
        {
            plugin.Configuration.DesynthSource = Enum.Parse<DesynthSource>(sources[sourceIndex]);
            plugin.SaveConfiguration();
        }

        var scopeIndex = Math.Max(0, Array.FindIndex(ScopeOptions, x => x.Scope == plugin.Configuration.DesynthInventoryScope));
        var scopeLabels = ScopeOptions.Select(x => x.Label).ToArray();
        if (ImGui.Combo("Source scope", ref scopeIndex, scopeLabels, scopeLabels.Length))
        {
            DesynthPolicyService.ApplyScopeToConfiguration(plugin.Configuration, ScopeOptions[scopeIndex].Scope);
            plugin.SaveConfiguration();
        }

        var skillups = plugin.Configuration.DesynthSkillUpFilterEnabled;
        if (ImGui.Checkbox("Skill-up filter", ref skillups))
        {
            plugin.Configuration.DesynthSkillUpFilterEnabled = skillups;
            plugin.SaveConfiguration();
        }

        var threshold = plugin.Configuration.DesynthSkillUpThreshold;
        if (ImGui.InputInt("Skill-up threshold", ref threshold))
        {
            plugin.Configuration.DesynthSkillUpThreshold = Math.Clamp(threshold, 0, 1000);
            plugin.SaveConfiguration();
        }

        var contextMenu = plugin.Configuration.DesynthContextMenuEnabled;
        if (ImGui.Checkbox("Inventory context-menu preset action", ref contextMenu))
        {
            plugin.Configuration.DesynthContextMenuEnabled = contextMenu;
            plugin.SaveConfiguration();
        }
    }

    private void DrawPresets()
    {
        ImGui.TextUnformatted("Local presets");
        var presetNames = plugin.DesynthPresetStore.Presets.Select(x => x.Name).ToArray();
        var presetIndex = Math.Max(0, Array.FindIndex(presetNames, x => string.Equals(x, plugin.Configuration.DesynthActivePreset, StringComparison.OrdinalIgnoreCase)));
        if (ImGui.Combo("Active preset", ref presetIndex, presetNames, presetNames.Length))
            SetStatus(plugin.SelectDesynthPreset(presetNames[presetIndex], out var error), error);

        var active = plugin.DesynthPresetStore.Get(plugin.Configuration.DesynthActivePreset);
        ImGui.TextDisabled($"{active.ItemIds.Count} item(s): {string.Join(", ", active.ItemIds.Take(20).Select(FormatItem))}{(active.ItemIds.Count > 20 ? "..." : string.Empty)}");

        ImGui.InputText("New preset", ref newPresetName, 80);
        ImGui.InputText("Description", ref newPresetDescription, 200);
        if (ImGui.Button("Create preset"))
            SetStatus(plugin.DesynthPresetStore.Create(newPresetName, newPresetDescription, out var error), error);

        ImGui.InputText("Rename active to", ref renamePresetName, 80);
        if (ImGui.Button("Rename active"))
        {
            var oldName = active.Name;
            var success = plugin.RenameDesynthPreset(oldName, renamePresetName, out var error);
            SetStatus(success, error);
        }
        ImGui.SameLine();
        if (ImGui.Button("Delete active"))
            SetStatus(plugin.DeleteDesynthPreset(active.Name, out var error), error);

        if (DrawItemSearchDropdown("Desynth item", ref itemSearch, desynthableItems, ref selectedItemId, ref selectedItemName))
            itemText = selectedItemId.ToString();

        ImGui.InputText("Item ID or exact name", ref itemText, 160);
        if (ImGui.Button("Add item"))
            SetStatus(plugin.TryMutateActiveDesynthPresetItem(GetSelectedItemValue(), true, out var error), error);
        ImGui.SameLine();
        if (ImGui.Button("Remove item"))
            SetStatus(plugin.TryMutateActiveDesynthPresetItem(GetSelectedItemValue(), false, out var error), error);

        ImGui.InputTextMultiline("JSON / base64", ref importExportText, 262144, new Vector2(-1f, 130f));
        if (ImGui.Button("Export JSON"))
            importExportText = plugin.DesynthPresetStore.ExportRaw();
        ImGui.SameLine();
        if (ImGui.Button("Export base64"))
            importExportText = plugin.DesynthPresetStore.ExportBase64();
        ImGui.SameLine();
        if (ImGui.Button("Import JSON"))
            SetStatus(plugin.ImportDesynthPresetsRaw(importExportText, out var rawError), rawError);
        ImGui.SameLine();
        if (ImGui.Button("Import base64"))
            SetStatus(plugin.ImportDesynthPresetsBase64(importExportText, out var base64Error), base64Error);
    }

    private void DrawLedger()
    {
        ImGui.TextUnformatted("Last-duty ledger");
        ImGui.TextWrapped(plugin.DesynthDutyLedgerStore.LastStatus);
        if (ImGui.Button("Clear ledger"))
            plugin.DesynthDutyLedgerStore.Clear();
    }

    private void SetStatus(bool success, string error)
        => status = success ? "Saved." : error;

    private static string ModeLabel(DesynthRunMode mode)
        => DesynthPolicyService.GetModeName(mode);

    private string GetSelectedItemValue()
        => selectedItemId > 0 ? selectedItemId.ToString() : itemText;

    private string FormatItem(uint itemId)
    {
        var item = desynthableItems.FirstOrDefault(x => x.Id == itemId);
        return string.IsNullOrEmpty(item.Name) ? itemId.ToString() : $"{item.Name} ({itemId})";
    }

    private void EnsureDesynthItemsLoaded()
    {
        if (itemsLoaded)
            return;

        itemsLoaded = true;
        try
        {
            foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
            {
                if (item.RowId == 0 || item.Desynth == 0)
                    continue;

                var name = item.Name.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                desynthableItems.Add((item.RowId, name));
            }

            desynthableItems.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            status = $"Failed to load desynthable item search data: {ex.Message}";
            Plugin.Log.Warning(ex, "[ADS][Desynth] Failed to load desynthable item search data.");
        }
    }

    private static bool DrawItemSearchDropdown(string label, ref string search, List<(uint Id, string Name)> items, ref uint selectedId, ref string selectedName)
    {
        var changed = false;
        var displayText = selectedId > 0 ? $"{selectedName} ({selectedId})" : $"Select {label}...";

        ImGui.SetNextItemWidth(420f);
        if (ImGui.BeginCombo($"##{label}Select", displayText))
        {
            ImGui.SetNextItemWidth(400f);
            ImGui.InputText($"Search##{label}", ref search, 128);
            ImGui.Separator();

            const int maxResults = 25;
            var shown = 0;
            if (!string.IsNullOrWhiteSpace(search) && search.Length >= 2)
            {
                var searchLower = search.ToLowerInvariant();
                var isNumeric = uint.TryParse(search, out _);

                for (var index = 0; index < items.Count && shown < maxResults; index++)
                {
                    var item = items[index];
                    var match = isNumeric
                        ? item.Id.ToString().Contains(search, StringComparison.Ordinal)
                        : item.Name.Contains(searchLower, StringComparison.OrdinalIgnoreCase);
                    if (!match)
                        continue;

                    shown++;
                    var isSelected = item.Id == selectedId;
                    if (ImGui.Selectable($"{item.Name} ({item.Id})##{label}{index}", isSelected))
                    {
                        selectedId = item.Id;
                        selectedName = item.Name;
                        changed = true;
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }

                if (shown == 0)
                    ImGui.TextDisabled("No results.");
            }
            else
            {
                ImGui.TextDisabled("Type at least 2 characters to search.");
            }

            ImGui.EndCombo();
        }

        return changed;
    }
}
