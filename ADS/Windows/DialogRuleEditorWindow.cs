using System.Numerics;
using ADS.Models;
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
        Size = new Vector2(1680f, 760f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();
        EnsureDraftLoaded();

        ImGui.TextWrapped("Spreadsheet-style editor for dialog-yesno-rules.json. These rules are global SelectYesno text matches, not duty-scoped.");
        ImGui.TextWrapped("When ADS owns a supported duty and SelectYesno appears, the first enabled matching rule decides whether ADS clicks Yes or No.");
        ImGui.TextWrapped(plugin.DialogYesNoRuleService.ConfigPath);
        ImGui.TextWrapped(editorStatus);
        if (dirty)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.97f, 0.84f, 0.31f, 1f));
            ImGui.TextUnformatted("Unsaved dialog rule edits");
            ImGui.PopStyleColor();
        }

        if (ImGui.Button("+ Row"))
        {
            draft.Rules.Add(plugin.DialogYesNoRuleService.CreateBlankRule());
            dirty = true;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!dirty);
        if (ImGui.Button("Save"))
        {
            if (plugin.DialogYesNoRuleService.SaveManifest(draft))
            {
                RefreshDraft("Dialog rules saved and reloaded.");
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
            plugin.DialogYesNoRuleService.Reload();
            RefreshDraft("Dialog-rule draft reloaded from disk.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Open JSON"))
            plugin.OpenPath(plugin.DialogYesNoRuleService.ConfigPath);

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
        draft = plugin.DialogYesNoRuleService.CreateEditableCopy();
        draftLoaded = true;
        dirty = false;
        editorStatus = status;
    }

    private void DrawRulesTable()
    {
        const ImGuiTableFlags tableFlags =
            ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollX
            | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("ADSDialogRulesTable", 6, tableFlags, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 40f);
        ImGui.TableSetupColumn("Match", ImGuiTableColumnFlags.WidthFixed, 100f);
        ImGui.TableSetupColumn("Prompt Pattern", ImGuiTableColumnFlags.WidthStretch, 520f);
        ImGui.TableSetupColumn("Response", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthStretch, 420f);
        ImGui.TableSetupColumn("-", ImGuiTableColumnFlags.WidthFixed, 30f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

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
            var matchModeIndex = Math.Max(0, Array.IndexOf(MatchModes, string.IsNullOrWhiteSpace(rule.MatchMode) ? "Contains" : rule.MatchMode));
            if (ImGui.Combo("##MatchMode", ref matchModeIndex, MatchModes, MatchModes.Length))
            {
                rule.MatchMode = MatchModes[matchModeIndex];
                dirty = true;
            }

            ImGui.TableSetColumnIndex(2);
            if (EditTextCell("##PromptPattern", rule.PromptPattern, 256, out var promptPattern))
            {
                rule.PromptPattern = promptPattern;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(3);
            var responseIndex = Math.Max(0, Array.IndexOf(ResponseLabels, string.IsNullOrWhiteSpace(rule.Response) ? "Yes" : rule.Response));
            if (ImGui.Combo("##Response", ref responseIndex, ResponseLabels, ResponseLabels.Length))
            {
                rule.Response = ResponseLabels[responseIndex];
                dirty = true;
            }

            ImGui.TableSetColumnIndex(4);
            if (EditTextCell("##Notes", rule.Notes, 256, out var notes))
            {
                rule.Notes = notes;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(5);
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
}
