using System.Numerics;
using ADS.Models;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace ADS.Windows;

public sealed class DesynthesisWindow : PositionedWindow, IDisposable
{
    private static readonly DesynthRunMode[] RunModes =
    [
        DesynthRunMode.Configured,
        DesynthRunMode.All,
        DesynthRunMode.Whitelist,
        DesynthRunMode.LastDuty,
        DesynthRunMode.Skillups,
        DesynthRunMode.InventoryOnly,
        DesynthRunMode.EverywhereSkipGearsets,
        DesynthRunMode.Everywhere,
    ];

    private static readonly (DesynthSource Source, string Label)[] SourceOptions =
    [
        (DesynthSource.ActiveWhitelist, "Items in active preset"),
        (DesynthSource.AllInventory, "All eligible items"),
        (DesynthSource.LastDutyGains, "Items gained in last completed duty"),
    ];

    private static readonly (DesynthInventoryScope Scope, string Label)[] ScopeOptions =
    [
        (DesynthInventoryScope.InventoryOnly, "Inventory only"),
        (DesynthInventoryScope.InventoryAndArmourySkipGearsets, "Inventory + armoury, skip gearsets"),
        (DesynthInventoryScope.InventoryAndArmoury, "Inventory + armoury"),
    ];

    private readonly Plugin plugin;
    private string newPresetName = string.Empty;
    private string renamePresetName = string.Empty;
    private string itemSearch = string.Empty;
    private uint selectedItemId;
    private string selectedItemName = string.Empty;
    private string status = string.Empty;
    private int runModeIndex;
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
        ImGui.TextUnformatted("Run");
        ImGui.TextUnformatted($"Status: {plugin.UtilityAutomationService.StatusMessage}");
        ImGui.TextDisabled($"Mode: {plugin.UtilityAutomationService.ActiveDesynthModeName}");
        ImGui.TextDisabled($"Scope: {plugin.UtilityAutomationService.ActiveDesynthScopeName}");
        ImGui.TextDisabled($"Eligible: {plugin.UtilityAutomationService.DesynthEligibleCount}; completed: {plugin.UtilityAutomationService.DesynthCompletedCount}");

        var modeLabels = RunModes.Select(ModeDisplayLabel).ToArray();
        ImGui.SetNextItemWidth(300f);
        ImGui.Combo("Run mode", ref runModeIndex, modeLabels, modeLabels.Length);
        if (ImGui.Button("Start"))
            plugin.StartDesynth(ModeLabel(RunModes[runModeIndex]));
        ImGui.SameLine();
        ImGui.BeginDisabled(!plugin.UtilityAutomationService.IsDesynthRunning);
        if (ImGui.Button("Stop Desynthesis"))
            plugin.CancelUtility();
        ImGui.EndDisabled();
    }

    private void DrawPolicy()
    {
        ImGui.TextUnformatted("Policy");
        var sourceIndex = Math.Max(0, Array.FindIndex(SourceOptions, x => x.Source == plugin.Configuration.DesynthSource));
        var sourceLabels = SourceOptions.Select(x => x.Label).ToArray();
        if (ImGui.Combo("Choose items from", ref sourceIndex, sourceLabels, sourceLabels.Length))
        {
            plugin.Configuration.DesynthSource = SourceOptions[sourceIndex].Source;
            plugin.SaveConfiguration();
        }

        var scopeIndex = Math.Max(0, Array.FindIndex(ScopeOptions, x => x.Scope == plugin.Configuration.DesynthInventoryScope));
        var scopeLabels = ScopeOptions.Select(x => x.Label).ToArray();
        if (ImGui.Combo("Search these inventories", ref scopeIndex, scopeLabels, scopeLabels.Length))
        {
            DesynthPolicyService.ApplyScopeToConfiguration(plugin.Configuration, ScopeOptions[scopeIndex].Scope);
            plugin.SaveConfiguration();
        }

        var skillups = plugin.Configuration.DesynthSkillUpFilterEnabled;
        if (ImGui.Checkbox("Only desynthesize items that can grant skill", ref skillups))
        {
            plugin.Configuration.DesynthSkillUpFilterEnabled = skillups;
            plugin.SaveConfiguration();
        }

        var threshold = plugin.Configuration.DesynthSkillUpThreshold;
        if (ImGui.InputInt("Skill-up item-level allowance", ref threshold))
        {
            plugin.Configuration.DesynthSkillUpThreshold = Math.Clamp(threshold, 0, 1000);
            plugin.SaveConfiguration();
        }

        var contextMenu = plugin.Configuration.DesynthContextMenuEnabled;
        if (ImGui.Checkbox("Show preset action in inventory context menu", ref contextMenu))
        {
            plugin.Configuration.DesynthContextMenuEnabled = contextMenu;
            plugin.SaveConfiguration();
        }
    }

    private void DrawPresets()
    {
        ImGui.TextUnformatted("Presets");
        var presetNames = plugin.DesynthPresetStore.Presets.Select(x => x.Name).ToArray();
        var presetIndex = Math.Max(0, Array.FindIndex(presetNames, x => string.Equals(x, plugin.Configuration.DesynthActivePreset, StringComparison.OrdinalIgnoreCase)));
        if (ImGui.Combo("Active preset", ref presetIndex, presetNames, presetNames.Length))
            SetStatus(plugin.SelectDesynthPreset(presetNames[presetIndex], out var error), error);

        var active = plugin.DesynthPresetStore.Get(plugin.Configuration.DesynthActivePreset);
        ImGui.TextDisabled($"{active.ItemIds.Count} item(s) in {active.Name}");

        ImGui.InputText("New preset", ref newPresetName, 80);
        if (ImGui.Button("Create preset"))
        {
            var success = plugin.DesynthPresetStore.Create(newPresetName, string.Empty, out var error);
            SetStatus(success, error);
            if (success)
                newPresetName = string.Empty;
        }

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

        DrawItemSearchDropdown("Desynth item", ref itemSearch, desynthableItems, ref selectedItemId, ref selectedItemName);
        ImGui.BeginDisabled(selectedItemId == 0);
        if (ImGui.Button("Add Selected Item"))
        {
            var success = plugin.TryMutateActiveDesynthPresetItem(selectedItemId.ToString(), true, out var error);
            SetStatus(success, error);
            if (success)
            {
                selectedItemId = 0;
                selectedItemName = string.Empty;
                itemSearch = string.Empty;
            }
        }
        ImGui.EndDisabled();

        DrawActivePresetItems(active);

        if (ImGui.Button("Copy Presets"))
        {
            ImGui.SetClipboardText(plugin.DesynthPresetStore.ExportRaw());
            status = "Copied formatted preset JSON to clipboard.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Import Presets"))
            SetStatus(plugin.ImportDesynthPresetsClipboard(ImGui.GetClipboardText() ?? string.Empty, out var error), error);
    }

    private void DrawLedger()
    {
        ImGui.TextUnformatted("Last-Duty Ledger");
        ImGui.TextWrapped(plugin.DesynthDutyLedgerStore.LastStatus);
        if (ImGui.Button("Clear ledger"))
            plugin.DesynthDutyLedgerStore.Clear();
    }

    private void SetStatus(bool success, string error)
        => status = success ? "Saved." : error;

    private static string ModeLabel(DesynthRunMode mode)
        => DesynthPolicyService.GetModeName(mode);

    private static string ModeDisplayLabel(DesynthRunMode mode)
        => mode switch
        {
            DesynthRunMode.Configured => "Configured policy",
            DesynthRunMode.All => "All eligible items",
            DesynthRunMode.Whitelist => "Active preset items",
            DesynthRunMode.LastDuty => "Last-duty gains",
            DesynthRunMode.Skillups => "Skill-up items",
            DesynthRunMode.InventoryOnly => "Inventory only",
            DesynthRunMode.EverywhereSkipGearsets => "Inventory + armoury, skip gearsets",
            DesynthRunMode.Everywhere => "Inventory + armoury, include gearsets",
            _ => ModeLabel(mode),
        };

    private string GetItemName(uint itemId)
    {
        var item = desynthableItems.FirstOrDefault(x => x.Id == itemId);
        return string.IsNullOrEmpty(item.Name) ? "Unknown item" : item.Name;
    }

    private void DrawActivePresetItems(DesynthPreset active)
    {
        ImGui.TextUnformatted("Active preset contents");
        if (active.ItemIds.Count == 0)
        {
            ImGui.TextDisabled("No items in active preset.");
            return;
        }

        if (!ImGui.BeginTable(
                "ADSDesynthPresetItems",
                3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, 190f)))
        {
            return;
        }

        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableHeadersRow();
        foreach (var itemId in active.ItemIds.ToArray())
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(GetItemName(itemId));
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(itemId.ToString());
            ImGui.TableSetColumnIndex(2);
            if (ImGui.SmallButton($"Remove##ADSDesynthPresetRemove{itemId}"))
                SetStatus(plugin.DesynthPresetStore.RemoveItem(active.Name, itemId, out var error), error);
        }

        ImGui.EndTable();
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
