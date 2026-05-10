using System.Numerics;
using System.Text.Json;
using ADS.Models;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class DialogRuleEditorWindow : PositionedWindow, IDisposable
{
    private static readonly string[] MatchModes =
    [
        "Contains",
        "Exact",
    ];

    private static readonly string[] ResponseLabels =
    [
        "Yes",
        "No",
    ];

    private readonly Plugin plugin;
    private DialogYesNoRuleManifest draft = new();
    private bool draftLoaded;
    private bool dirty;
    private string selectedPresetName = DialogYesNoRuleService.DefaultPresetName;
    private string pendingPresetName = string.Empty;
    private string diskTransferPath = string.Empty;
    private string editorStatus = "Dialog rules not loaded.";

    public DialogRuleEditorWindow(Plugin plugin)
        : base("ADS Dialog Rules###ADSDialogRules")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(980f, 420f),
            MaximumSize = new Vector2(3200f, 2200f),
        };
        Size = new Vector2(1900f, 820f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();
        EnsureDraftLoaded();

        ImGui.TextWrapped("Spreadsheet-style editor for dialog-yesno-rules.json. These rules are global dialog matches, not duty-scoped.");
        ImGui.TextWrapped("Processing scope follows Settings > Process dialog rules outside owned duties.");
        ImGui.TextWrapped("Default Addon is SelectYesno. Optional Notification/NotificationCB can restore minimized prompts before ADS clicks.");
        ImGui.TextWrapped($"Preset: {selectedPresetName} -> {plugin.DialogYesNoRuleService.GetPresetPath(selectedPresetName)}");
        if (!plugin.DialogYesNoRuleService.IsDefaultPreset(selectedPresetName))
            ImGui.TextWrapped("Runtime ADS still reads DEFAULT/live dialog rules. Non-default dialog presets are parked datasets until you import or copy them back into DEFAULT.");
        ImGui.TextWrapped(editorStatus);
        if (dirty)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.97f, 0.84f, 0.31f, 1f));
            ImGui.TextUnformatted("Unsaved dialog rule edits");
            ImGui.PopStyleColor();
        }

        DrawPresetToolbar();
        ImGui.SameLine();
        if (ImGui.Button("+ Row"))
        {
            draft.Rules.Add(plugin.DialogYesNoRuleService.CreateBlankRule());
            dirty = true;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!dirty);
        if (ImGui.Button("Save"))
        {
            if (plugin.DialogYesNoRuleService.SaveManifest(selectedPresetName, draft))
            {
                RefreshDraft($"Saved dialog preset {selectedPresetName}.");
            }
            else
            {
                editorStatus = plugin.DialogYesNoRuleService.LastLoadStatus;
            }
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Reload From Disk"))
        {
            RefreshDraft($"Dialog-rule preset {selectedPresetName} reloaded from disk.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Open JSON"))
            plugin.OpenPath(plugin.DialogYesNoRuleService.GetPresetPath(selectedPresetName));

        ImGui.Spacing();
        DrawRulesTable();
    }

    private void EnsureDraftLoaded()
    {
        if (draftLoaded)
            return;

        RefreshDraft("Dialog rules loaded from disk.");
    }

    private void RefreshDraft(string status)
    {
        if (!plugin.DialogYesNoRuleService.TryLoadManifest(selectedPresetName, out draft, out var loadStatus))
        {
            draft = new DialogYesNoRuleManifest();
            draftLoaded = true;
            dirty = false;
            editorStatus = loadStatus;
            SyncDiskTransferPath();
            return;
        }

        draftLoaded = true;
        dirty = false;
        editorStatus = $"{status} {loadStatus}";
        SyncDiskTransferPath();
    }

    private void DrawPresetToolbar()
    {
        ImGui.TextUnformatted("Preset");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("##DialogRulePreset", selectedPresetName))
        {
            foreach (var presetName in plugin.DialogYesNoRuleService.GetPresetNames())
            {
                var isSelected = string.Equals(presetName, selectedPresetName, StringComparison.OrdinalIgnoreCase);
                if (!ImGui.Selectable(presetName, isSelected))
                    continue;

                SwitchPreset(presetName);
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Export##DialogPreset"))
            ExportManifestToClipboard();

        ImGui.SameLine();
        if (ImGui.SmallButton("Import##DialogPreset"))
            ImportManifestFromClipboard();

        ImGui.SameLine();
        if (ImGui.SmallButton("Disk+##DialogPreset"))
        {
            SyncDiskTransferPath();
            ImGui.OpenPopup("ADSDialogPresetDiskTransfer");
        }

        DrawDiskTransferPopup();

        ImGui.SameLine();
        if (ImGui.SmallButton("+##DialogPreset"))
        {
            pendingPresetName = plugin.DialogYesNoRuleService.IsDefaultPreset(selectedPresetName)
                ? "Preset"
                : plugin.DialogYesNoRuleService.SanitizePresetName(selectedPresetName);
            ImGui.OpenPopup("ADSCreateDialogPreset");
        }

        DrawCreatePresetPopup();

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(plugin.DialogYesNoRuleService.IsDefaultPreset(selectedPresetName)))
        {
            if (ImGui.SmallButton("-##DialogPreset"))
                DeleteCurrentPreset();
        }

        if (plugin.DialogYesNoRuleService.IsDefaultPreset(selectedPresetName))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("@##DialogPreset"))
                ResetDefaultDraftFromCache();
        }
    }

    private void SwitchPreset(string presetName)
    {
        if (string.Equals(presetName, selectedPresetName, StringComparison.OrdinalIgnoreCase))
            return;

        var previousPreset = selectedPresetName;
        var discardedDirtyDraft = dirty;
        selectedPresetName = presetName;
        RefreshDraft(
            discardedDirtyDraft
                ? $"Switched from {previousPreset} to {selectedPresetName}; unsaved edits in the previous draft were discarded."
                : $"Switched from {previousPreset} to {selectedPresetName}.");
    }

    private void DrawCreatePresetPopup()
    {
        if (!ImGui.BeginPopup("ADSCreateDialogPreset"))
            return;

        ImGui.TextUnformatted("Create preset from the current draft");
        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##NewDialogPresetName", "preset name", ref pendingPresetName, 64);

        if (ImGui.Button("Create##DialogPreset"))
        {
            var sanitizedName = plugin.DialogYesNoRuleService.SanitizePresetName(pendingPresetName);
            if (plugin.DialogYesNoRuleService.IsDefaultPreset(sanitizedName))
            {
                editorStatus = "DEFAULT is reserved; choose a different dialog preset name.";
            }
            else if (plugin.DialogYesNoRuleService.SaveManifest(sanitizedName, draft))
            {
                selectedPresetName = sanitizedName;
                draft = CloneManifest(draft);
                dirty = false;
                editorStatus = $"Created dialog preset {sanitizedName} from the current draft.";
                SyncDiskTransferPath();
                ImGui.CloseCurrentPopup();
            }
            else
            {
                editorStatus = plugin.DialogYesNoRuleService.LastLoadStatus;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##DialogPreset"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawDiskTransferPopup()
    {
        if (!ImGui.BeginPopup("ADSDialogPresetDiskTransfer"))
            return;

        ImGui.TextUnformatted("Full-manifest disk import/export");
        ImGui.SetNextItemWidth(540f);
        ImGui.InputTextWithHint("##DialogPresetDiskPath", "path to .json file", ref diskTransferPath, 512);

        if (ImGui.Button("Import file##DialogPreset"))
        {
            if (plugin.DialogYesNoRuleService.TryImportManifestFromPath(diskTransferPath, out var manifest, out var status))
            {
                draft = manifest;
                dirty = true;
                editorStatus = $"Imported full dialog manifest from disk into preset {selectedPresetName} draft. {status}";
            }
            else
            {
                editorStatus = status;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Export file##DialogPreset"))
        {
            if (plugin.DialogYesNoRuleService.TryExportManifestToPath(diskTransferPath, draft, out var status))
                editorStatus = status;
            else
                editorStatus = status;
        }

        ImGui.SameLine();
        if (ImGui.Button("Use preset path##DialogPreset"))
            SyncDiskTransferPath();

        ImGui.SameLine();
        if (ImGui.Button("Open preset dir##DialogPreset"))
            plugin.OpenPath(plugin.DialogYesNoRuleService.PresetDirectoryPath);

        ImGui.EndPopup();
    }

    private void DeleteCurrentPreset()
    {
        if (plugin.DialogYesNoRuleService.TryDeletePreset(selectedPresetName, out var status))
        {
            selectedPresetName = DialogYesNoRuleService.DefaultPresetName;
            RefreshDraft($"{status} Switched back to DEFAULT.");
            return;
        }

        editorStatus = status;
    }

    private void ResetDefaultDraftFromCache()
    {
        if (plugin.DialogYesNoRuleService.TryLoadDefaultCacheManifest(out var cacheManifest, out var status))
        {
            draft = cacheManifest;
            dirty = true;
            editorStatus = $"Loaded the current DEFAULT dialog cache into the draft. {status} Press Save to write it live.";
            return;
        }

        editorStatus = status;
    }

    private void ExportManifestToClipboard()
    {
        try
        {
            var json = JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true });
            ImGui.SetClipboardText(json);
            editorStatus = $"Copied full dialog preset {selectedPresetName} manifest JSON to the clipboard.";
        }
        catch (Exception ex)
        {
            editorStatus = $"Failed to export dialog preset {selectedPresetName}: {ex.Message}";
        }
    }

    private void ImportManifestFromClipboard()
    {
        if (plugin.DialogYesNoRuleService.TryImportManifestText(ImGui.GetClipboardText() ?? string.Empty, out var manifest, out var status))
        {
            draft = manifest;
            dirty = true;
            editorStatus = $"Imported full dialog manifest from clipboard into preset {selectedPresetName} draft. {status}";
            return;
        }

        editorStatus = status;
    }

    private void SyncDiskTransferPath()
        => diskTransferPath = plugin.DialogYesNoRuleService.GetPresetPath(selectedPresetName);

    private static DialogYesNoRuleManifest CloneManifest(DialogYesNoRuleManifest manifest)
        => new()
        {
            SchemaVersion = manifest.SchemaVersion,
            Description = manifest.Description,
            Rules = manifest.Rules.Select(
                    rule => new DialogYesNoRule
                    {
                        Enabled = rule.Enabled,
                        Addon = rule.Addon,
                        PromptPattern = rule.PromptPattern,
                        MatchMode = rule.MatchMode,
                        Response = rule.Response,
                        Delay = rule.Delay,
                        Notification = rule.Notification,
                        NotificationCB = rule.NotificationCB,
                        Notes = rule.Notes,
                    })
                .ToList(),
        };

    private void DrawRulesTable()
    {
        const ImGuiTableFlags tableFlags =
            ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollX
            | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("ADSDialogRulesTable", 10, tableFlags, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 40f);
        ImGui.TableSetupColumn("Addon", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("Match", ImGuiTableColumnFlags.WidthFixed, 100f);
        ImGui.TableSetupColumn("Prompt Pattern", ImGuiTableColumnFlags.WidthStretch, 420f);
        ImGui.TableSetupColumn("Response", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Delay", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Notification", ImGuiTableColumnFlags.WidthFixed, 180f);
        ImGui.TableSetupColumn("NotificationCB", ImGuiTableColumnFlags.WidthFixed, 240f);
        ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthStretch, 320f);
        ImGui.TableSetupColumn("-", ImGuiTableColumnFlags.WidthFixed, 30f);
        ImGui.TableSetupScrollFreeze(0, 1);
        DrawHeaderRow();

        var rowToRemove = -1;
        for (var i = 0; i < draft.Rules.Count; i++)
        {
            var rule = draft.Rules[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##Enabled", ref enabled))
            {
                rule.Enabled = enabled;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(1);
            if (EditTextCell("##Addon", string.IsNullOrWhiteSpace(rule.Addon) ? "SelectYesno" : rule.Addon, 64, out var addon))
            {
                rule.Addon = string.IsNullOrWhiteSpace(addon) ? "SelectYesno" : addon;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(2);
            var matchModeIndex = Math.Max(0, Array.IndexOf(MatchModes, string.IsNullOrWhiteSpace(rule.MatchMode) ? "Contains" : rule.MatchMode));
            if (ImGui.Combo("##MatchMode", ref matchModeIndex, MatchModes, MatchModes.Length))
            {
                rule.MatchMode = MatchModes[matchModeIndex];
                dirty = true;
            }

            ImGui.TableSetColumnIndex(3);
            if (EditTextCell("##PromptPattern", rule.PromptPattern, 256, out var promptPattern))
            {
                rule.PromptPattern = promptPattern;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(4);
            var responseIndex = Math.Max(0, Array.IndexOf(ResponseLabels, string.IsNullOrWhiteSpace(rule.Response) ? "Yes" : rule.Response));
            if (ImGui.Combo("##Response", ref responseIndex, ResponseLabels, ResponseLabels.Length))
            {
                rule.Response = ResponseLabels[responseIndex];
                dirty = true;
            }

            ImGui.TableSetColumnIndex(5);
            var delaySeconds = Math.Max(0, (int)Math.Round(rule.Delay));
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputInt("##Delay", ref delaySeconds))
            {
                rule.Delay = Math.Max(0, delaySeconds);
                dirty = true;
            }

            ImGui.TableSetColumnIndex(6);
            if (EditTextCell("##Notification", rule.Notification, 96, out var notification))
            {
                rule.Notification = notification;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(7);
            if (EditTextCell("##NotificationCB", rule.NotificationCB, 160, out var notificationCb))
            {
                rule.NotificationCB = notificationCb;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(8);
            if (EditTextCell("##Notes", rule.Notes, 256, out var notes))
            {
                rule.Notes = notes;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(9);
            if (ImGui.SmallButton("-"))
                rowToRemove = i;

            ImGui.PopID();
        }

        if (rowToRemove >= 0)
        {
            draft.Rules.RemoveAt(rowToRemove);
            dirty = true;
        }

        ImGui.EndTable();
    }

    private static void DrawHeaderRow()
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        DrawHeaderCell(0, "On", "Enable this rule.");
        DrawHeaderCell(1, "Addon", "Addon to watch. Leave as SelectYesno for normal yes/no prompts.");
        DrawHeaderCell(2, "Match", "Contains or Exact prompt matching.");
        DrawHeaderCell(3, "Prompt Pattern", "Text to match from the Addon prompt. Required for SelectYesno clicks.");
        DrawHeaderCell(4, "Response", "Yes or No for SelectYesno.");
        DrawHeaderCell(5, "Delay", "Seconds the addon or notification must stay visible before ADS acts.");
        DrawHeaderCell(6, "Notification", "Optional minimized notification addon to restore before the watched Addon is visible.");
        DrawHeaderCell(7, "NotificationCB", "Optional callback text, for example: _Notification true 0 16.");
        DrawHeaderCell(8, "Notes", "Human explanation only.");
        DrawHeaderCell(9, "-", "Delete row.");
    }

    private static void DrawHeaderCell(int column, string label, string tooltip)
    {
        ImGui.TableSetColumnIndex(column);
        ImGui.TableHeader(label);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private static bool EditTextCell(string id, string value, int maxLength, out string editedValue)
    {
        ImGui.SetNextItemWidth(-1f);
        var local = value;
        editedValue = local;
        if (!ImGui.InputText(id, ref local, maxLength))
            return false;

        editedValue = local;
        return true;
    }

    private readonly ref struct ImGuiDisabledBlock
    {
        private readonly bool disabled;

        public ImGuiDisabledBlock(bool disabled)
        {
            this.disabled = disabled;
            if (disabled)
                ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (disabled)
                ImGui.EndDisabled();
        }
    }
}
