using System.Numerics;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class ObjectRuleEditorWindow : PositionedWindow, IDisposable
{
    private static readonly string[] NameMatchModes =
    [
        "Exact",
        "Contains",
    ];

    private static readonly string[] ClassificationLabels =
    [
        "(none)",
        "Ignored",
        "Follow",
        "Required",
        "Optional",
        "Expendable",
        "CombatFriendly",
        "TreasureCoffer",
        "MapXzDestination",
    ];

    private static readonly string[] ClassificationValues =
    [
        "",
        "Ignored",
        "Follow",
        "Required",
        "Optional",
        "Expendable",
        "CombatFriendly",
        "TreasureCoffer",
        "MapXzDestination",
    ];

    private readonly Plugin plugin;
    private ObjectPriorityRuleManifest draft = new();
    private bool draftLoaded;
    private bool dirty;
    private string editorStatus = "Rules not loaded.";

    public ObjectRuleEditorWindow(Plugin plugin)
        : base("ADS Rules Editor###ADSRulesEditor")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(1200f, 520f),
            MaximumSize = new Vector2(3600f, 2400f),
        };
        Size = new Vector2(2400f, 980f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();
        EnsureDraftLoaded();

        ImGui.TextWrapped("Spreadsheet-style editor for duty-object-rules.json. Use '+' to add a row, '-' to remove one, then Save to write the authoritative config file.");
        ImGui.TextWrapped("Classification = Ignored removes matching objects from ADS observation/planning. Required on BattleNpc makes ADS seek/kill by rule priority. Follow is BattleNpc-only: it turns a live NPC such as Cid into a live-only movement anchor that yields to real monsters/interactables and never becomes a ghost target. MapXzDestination plus DestinationType=MapXZ and Map XZ like 11.3,10.4 creates a manual no-live-object waypoint that ghosts at 1y XZ using the player's current Y. Non-BattleNpc Follow rules are ignored. Territory/CFC/BaseId zero means wildcard. MaxDistance zero means no cap.");
        ImGui.TextWrapped("Tip: this table is intentionally wide. Drag the window wider or use the horizontal scrollbar to reach Map XZ, distance, wait, notes, and delete columns.");
        ImGui.TextWrapped(plugin.ObjectPriorityRuleService.ConfigPath);
        ImGui.TextWrapped(editorStatus);
        if (dirty)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.97f, 0.84f, 0.31f, 1f));
            ImGui.TextUnformatted("Unsaved rule edits");
            ImGui.PopStyleColor();
        }

        if (ImGui.Button("+ Row"))
        {
            draft.Rules.Add(plugin.ObjectPriorityRuleService.CreateBlankRule());
            dirty = true;
        }

        ImGui.SameLine();
        using (new ImGuiDisabledBlock(!dirty))
        {
            if (ImGui.Button("Save"))
            {
                if (plugin.ObjectPriorityRuleService.SaveManifest(draft))
                {
                    RefreshDraft("Rules saved and reloaded.");
                }
                else
                {
                    editorStatus = plugin.ObjectPriorityRuleService.LastLoadStatus;
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Reload From Disk"))
        {
            plugin.ObjectPriorityRuleService.Reload();
            RefreshDraft("Draft reloaded from disk.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Open JSON"))
            plugin.OpenPath(plugin.ObjectPriorityRuleService.ConfigPath);

        ImGui.Spacing();
        DrawRulesTable();
    }

    private void EnsureDraftLoaded()
    {
        if (draftLoaded)
            return;

        RefreshDraft("Rules loaded from disk.");
    }

    private void RefreshDraft(string status)
    {
        draft = plugin.ObjectPriorityRuleService.CreateEditableCopy();
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

        if (!ImGui.BeginTable("ADSRulesEditorTable", 17, tableFlags, new Vector2(-1f, -1f)))
            return;

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 40f);
        ImGui.TableSetupColumn("Duty", ImGuiTableColumnFlags.WidthFixed, 260f);
        ImGui.TableSetupColumn("Terr", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("CFC", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("BaseId", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 260f);
        ImGui.TableSetupColumn("Match", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthFixed, 160f);
        ImGui.TableSetupColumn("Dest", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("Map XZ", ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableSetupColumn("Pri", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Wait", ImGuiTableColumnFlags.WidthFixed, 70f);
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
            if (EditTextCell("##DutyEnglishName", rule.DutyEnglishName, 128, out var dutyEnglishName))
            {
                rule.DutyEnglishName = dutyEnglishName;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(2);
            if (EditUintCell("##TerritoryTypeId", rule.TerritoryTypeId, out var territoryTypeId))
            {
                rule.TerritoryTypeId = territoryTypeId;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(3);
            if (EditUintCell("##ContentFinderConditionId", rule.ContentFinderConditionId, out var contentFinderConditionId))
            {
                rule.ContentFinderConditionId = contentFinderConditionId;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(4);
            if (EditTextCell("##ObjectKind", rule.ObjectKind, 32, out var objectKind))
            {
                rule.ObjectKind = objectKind;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(5);
            if (EditUintCell("##BaseId", rule.BaseId, out var baseId))
            {
                rule.BaseId = baseId;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(6);
            if (EditTextCell("##ObjectName", rule.ObjectName, 128, out var objectName))
            {
                rule.ObjectName = objectName;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(7);
            var matchModeIndex = Math.Max(0, Array.IndexOf(NameMatchModes, string.IsNullOrWhiteSpace(rule.NameMatchMode) ? "Exact" : rule.NameMatchMode));
            if (ImGui.Combo("##NameMatchMode", ref matchModeIndex, NameMatchModes, NameMatchModes.Length))
            {
                rule.NameMatchMode = NameMatchModes[matchModeIndex];
                dirty = true;
            }

            ImGui.TableSetColumnIndex(8);
            var classificationIndex = Math.Max(0, Array.IndexOf(ClassificationValues, rule.Classification ?? string.Empty));
            if (ImGui.Combo("##Classification", ref classificationIndex, ClassificationLabels, ClassificationLabels.Length))
            {
                rule.Classification = ClassificationValues[classificationIndex];
                dirty = true;
            }

            ImGui.TableSetColumnIndex(9);
            if (EditTextCell("##DestinationType", rule.DestinationType, 32, out var destinationType))
            {
                rule.DestinationType = destinationType;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(10);
            if (EditTextCell("##MapCoordinates", rule.MapCoordinates, 32, out var mapCoordinates))
            {
                rule.MapCoordinates = mapCoordinates;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(11);
            if (EditIntCell("##Priority", rule.Priority, out var priority))
            {
                rule.Priority = priority;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(12);
            if (EditFloatCell("##PriorityVerticalRadius", rule.PriorityVerticalRadius, out var priorityVerticalRadius))
            {
                rule.PriorityVerticalRadius = priorityVerticalRadius;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(13);
            if (EditNullableFloatCell("##MaxDistance", rule.MaxDistance, out var maxDistance))
            {
                rule.MaxDistance = maxDistance;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(14);
            if (EditFloatCell("##WaitAtDestinationSeconds", rule.WaitAtDestinationSeconds, out var waitAtDestinationSeconds))
            {
                rule.WaitAtDestinationSeconds = waitAtDestinationSeconds;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(15);
            if (EditTextCell("##Notes", rule.Notes, 256, out var notes))
            {
                rule.Notes = notes;
                dirty = true;
            }

            ImGui.TableSetColumnIndex(16);
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

    private static bool EditUintCell(string id, uint value, out uint editedValue)
    {
        ImGui.SetNextItemWidth(-1f);
        var local = value > int.MaxValue ? int.MaxValue : (int)value;
        editedValue = value;
        if (!ImGui.InputInt(id, ref local))
            return false;

        editedValue = local <= 0 ? 0u : (uint)local;
        return true;
    }

    private static bool EditIntCell(string id, int value, out int editedValue)
    {
        ImGui.SetNextItemWidth(-1f);
        var local = value;
        editedValue = value;
        if (!ImGui.InputInt(id, ref local))
            return false;

        editedValue = local;
        return true;
    }

    private static bool EditFloatCell(string id, float value, out float editedValue)
    {
        ImGui.SetNextItemWidth(-1f);
        var local = value;
        editedValue = value;
        if (!ImGui.InputFloat(id, ref local, 0f, 0f, "%.1f"))
            return false;

        editedValue = local < 0f ? 0f : local;
        return true;
    }

    private static bool EditNullableFloatCell(string id, float? value, out float? editedValue)
    {
        ImGui.SetNextItemWidth(-1f);
        var local = value ?? 0f;
        editedValue = value;
        if (!ImGui.InputFloat(id, ref local, 0f, 0f, "%.1f"))
            return false;

        editedValue = local <= 0f ? null : local;
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
