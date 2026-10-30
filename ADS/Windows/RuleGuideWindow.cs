using AethertekUI;
using ADS.Localization;
using System.Numerics;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class RuleGuideWindow : PositionedWindow, IDisposable
{
    private readonly Plugin? plugin;
    private ToolGuide tool;
    private int rulesTask;

    public RuleGuideWindow()
        : this(false)
    {
    }

    public RuleGuideWindow(bool compact)
        : this(null, compact)
    {
    }

    public RuleGuideWindow(Plugin? plugin, bool compact)
        : base("ADS Guide###ADSRuleGuide")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760f, 520f),
            MaximumSize = new Vector2(1800f, 1800f),
        };
        Size = compact ? new Vector2(840f, 620f) : new Vector2(1050f, 850f);
    }

    public void Dispose()
    {
    }

    internal void OpenForTool(ToolGuide selectedTool)
    {
        tool = selectedTool;
        if (IsOpen) MarkVisited();
        IsOpen = true;
    }

    public override void OnOpen() => MarkVisited();

    private void MarkVisited()
    {
        if (plugin is not null && WizardCatalog.MarkToolGuideVisited(plugin.Configuration, tool))
            plugin.SaveConfiguration();
    }

    public override void Draw()
    {
        DrawWindowChrome();
        using var structuralBackground = MaterialSkin.StructuralBackgroundStyle();
        FinalizePendingWindowPlacement();

        if (WindowLayout.SmallButton(Ui.L("Rules guide"))) OpenForTool(ToolGuide.Rules);
        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Object Explorer guide"))) OpenForTool(ToolGuide.ObjectExplorer);
        if (tool == ToolGuide.ObjectExplorer)
        {
            DrawExplorerGuide();
            return;
        }
        DrawRuleTasks();
        if (!MaterialText.CollapsingHeader(Ui.L("Class and field reference")))
            return;

        DrawSection("Quick Start");
        MaterialText.TextWrapped(Ui.T("Object Explorer -> RULE -> choose Class -> fill the relevant colored fields -> save an active custom preset -> retest from a clean enough state."));
        MaterialText.TextWrapped(Ui.T("Field cues: red = required, amber = recommended, normal = optional, dim = ignored by this class. Class selection and cues never clear ignored stored values."));

        DrawSection("How A Rule Wins");
        MaterialText.BulletText(Ui.T("1. Scope: Duty, Terr, CFC, then Layer."));
        MaterialText.BulletText(Ui.T("2. Object match: Kind, BaseId, Name/Match, then optional positional selector."));
        MaterialText.BulletText(Ui.T("3. Gates: candidates failing Dist or Y are removed before a winner is selected."));
        MaterialText.BulletText(Ui.T("4. Priority: lower Pri wins among eligible matching candidates."));
        MaterialText.BulletText(Ui.T("5. Behavior and timing: Class, Wait-before, and Wait-after control execution."));
        MaterialText.TextWrapped(Ui.T("For the same object, more specific matching scopes and selectors rank first; lower priority breaks ties. A rejected candidate lets the next eligible rule win."));
        MaterialText.TextWrapped(Ui.T("A failed higher-ranked candidate cannot shadow a lower eligible rule. Failed Required/BossFight/CombatFriendly BattleNpc rules are non-blocking; failed Ignored/Follow BattleNpc rules keep generic monster fallback."));

        DrawSection("Choose Class By Goal");
        if (ImGui.BeginTable("ADSRuleGuideGoals", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn(Ui.L("Class"), ImGuiTableColumnFlags.WidthFixed, 180f);
            ImGui.TableSetupColumn(Ui.L("Use When"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Ui.L("Common Example"), ImGuiTableColumnFlags.WidthStretch);
            WindowLayout.TableHeadersRow();
            foreach (var semantics in RuleSemanticsCatalog.Classifications)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                MaterialText.Text(Ui.Display(semantics.Label));
                ImGui.TableSetColumnIndex(1);
                MaterialText.TextWrapped(Ui.Display(semantics.Goal));
                ImGui.TableSetColumnIndex(2);
                MaterialText.TextWrapped(Ui.Display(semantics.CommonExample));
            }
            ImGui.EndTable();
        }

        DrawSection("Class Help And Field Matrix");
        MaterialText.TextWrapped(Ui.T("Use the ? button beside a row's Class for focused help. Expand a class below for full required/recommended/optional/ignored guidance."));
        foreach (var semantics in RuleSemanticsCatalog.Classifications)
        {
            if (!MaterialText.CollapsingHeader(Ui.L(semantics.Label)))
                continue;

            MaterialText.TextWrapped(Ui.Display(semantics.Behavior));
            MaterialText.TextWrapped(Ui.T("Relevant editor fields: {0}", string.Join(", ", RuleSemanticsCatalog.GetRelevantEditorFieldLabels(semantics).Select(Ui.Display))));
            if (ImGui.BeginTable($"ADSRuleGuideMatrix{semantics.Value}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn(Ui.L("Required"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(Ui.L("Recommended"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(Ui.L("Optional"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(Ui.L("Ignored"), ImGuiTableColumnFlags.WidthStretch);
                WindowLayout.TableHeadersRow();
                ImGui.TableNextRow();
                DrawFieldList(semantics, RuleFieldUse.Required, 0);
                DrawFieldList(semantics, RuleFieldUse.Recommended, 1);
                DrawFieldList(semantics, RuleFieldUse.Optional, 2);
                DrawFieldList(semantics, RuleFieldUse.Ignored, 3);
                ImGui.EndTable();
            }
        }

        DrawSection("Advanced Field Reference");
        if (ImGui.BeginTable("ADSRuleGuideGlossary", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn(Ui.L("JSON Field"), ImGuiTableColumnFlags.WidthFixed, 240f);
            ImGui.TableSetupColumn(Ui.L("Meaning"), ImGuiTableColumnFlags.WidthStretch);
            WindowLayout.TableHeadersRow();
            foreach (var field in RuleSemanticsCatalog.UniversalFields)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                MaterialText.Text(field);
                ImGui.TableSetColumnIndex(1);
                MaterialText.TextWrapped(Ui.Display(RuleSemanticsCatalog.FieldGlossary[field]));
            }
            ImGui.EndTable();
        }

        DrawSection("Coordinate And JSON Examples");
        MaterialText.TextWrapped(Ui.T("Map coordinates use X,Z: `11.3,10.4`. Ordinary/manual world coordinates use X,Y,Z: `154.1,101.9,-34.2`. Cardinal holds use world X,Z: `123.4,-56.7`; X,Y,Z is accepted and Y is ignored."));
        MaterialText.Text("""
{
  "classification": "CardinalHoldNorth",
  "worldCoordinates": "123.4,-56.7",
  "maxDistance": 3.0,
  "waitAtDestinationSeconds": 1.5,
  "priority": 100
}
""");
        MaterialText.TextWrapped(Ui.T("A cardinal hold activates only while ADS owns duty execution and the player is inside its X/Z radius. ADS stops vnav, holds direct movement for the full duration, releases input, then ghosts the row. Interrupted holds remain unconsumed."));
    }

    private void DrawRuleTasks()
    {
        string[] tasks = ["Use a preset", "Create a first rule", "Choose class and scope", "Why a rule does not win", "Save and share", "Crystal Tower presets"];
        ImGui.SetNextItemWidth(-1f);
        WindowLayout.Combo(Ui.L("Choose a task"), ref rulesTask, tasks.Select(task => Ui.T(task)).ToArray(), tasks.Length);
        switch (rulesTask)
        {
            case 0:
                MaterialText.TextWrapped(Ui.T("Object Rules shows one combined effective view, not one backing JSON file. DEFAULT owns indexed Global and territory shards; a custom preset inherits every DEFAULT context for which it has no override."));
                MaterialText.TextWrapped(Ui.T("Published presets are protected and updated automatically. Use New Preset to create a personal copy."));
                if (WindowLayout.Button(Ui.L("Crystal Tower presets"))) rulesTask = 5;
                break;
            case 1:
                MaterialText.TextWrapped(Ui.T("Object Explorer -> RULE -> choose Class -> fill the relevant colored fields -> save an active custom preset -> retest from a clean enough state."));
                MaterialText.TextWrapped(Ui.T("Field cues: red = required, amber = recommended, normal = optional, dim = ignored by this class. Class selection and cues never clear ignored stored values."));
                break;
            case 2:
                MaterialText.TextWrapped(Ui.T("Use the ? button beside a row's Class for focused help. Expand a class below for full required/recommended/optional/ignored guidance."));
                MaterialText.BulletText(Ui.T("1. Scope: Duty, Terr, CFC, then Layer."));
                MaterialText.BulletText(Ui.T("2. Object match: Kind, BaseId, Name/Match, then optional positional selector."));
                break;
            case 3:
                MaterialText.TextWrapped(Ui.T("For the same object, more specific matching scopes and selectors rank first; lower priority breaks ties. A rejected candidate lets the next eligible rule win."));
                MaterialText.BulletText(Ui.T("1. Scope: Duty, Terr, CFC, then Layer."));
                MaterialText.BulletText(Ui.T("2. Object match: Kind, BaseId, Name/Match, then optional positional selector."));
                MaterialText.BulletText(Ui.T("3. Gates: candidates failing Dist or Y are removed before a winner is selected."));
                MaterialText.BulletText(Ui.T("4. Priority: lower Pri wins among eligible matching candidates."));
                MaterialText.BulletText(Ui.T("5. Behavior and timing: Class, Wait-before, and Wait-after control execution."));
                break;
            case 4:
                MaterialText.TextWrapped(Ui.T("Saving a changed context writes its complete replacement, including rows hidden by text or row filters. An empty custom shard intentionally suppresses every inherited row in that context."));
                MaterialText.TextWrapped(Ui.T("Revert deletes each checked saved override. A custom-only context then disappears because DEFAULT has nothing to inherit."));
                MaterialText.TextWrapped(Ui.T("Import and Save creates this custom preset or merges only the included complete contexts into it, then activates it. Unrelated destination files are preserved."));
                MaterialText.TextWrapped(Ui.T("Published presets are protected and updated automatically. Use New Preset to create a personal copy."));
                MaterialText.TextWrapped(Ui.T("ADS copies complete saved shards and updates index.json once for new contexts. It never stages, commits, pushes, switches branches, or opens a pull request; GitHub submission stays manual."));
                break;
            case 5:
                MaterialText.Text("DEFAULT - SYNC - Crystal Tower x 3");
                MaterialText.Text("DEFAULT - UNSYNC - Crystal Tower x 3");
                MaterialText.TextWrapped(Ui.T("SYNC: normal add priorities. UNSYNC: boss focus. Choose queue mode separately; x 3 groups three raids."));
                foreach (var (cfc, territory) in new[] { (92u, 174u), (102u, 372u), (111u, 151u) })
                {
                    var duty = plugin?.DutyCatalogService.Entries.FirstOrDefault(entry => entry.ContentFinderConditionId == cfc);
                    MaterialText.Text($"{(duty is null ? string.Empty : Ui.DutyName(duty) + ": ")}{Ui.T("CFC")} {cfc} / {Ui.T("Territory")} {territory}");
                }
                MaterialText.TextWrapped(Ui.T("ADS owns progression; Fren Rider yields local following during handoff. Prepare combat and explicit DAD tasks before entry."));
                MaterialText.TextWrapped(Ui.T("Assign alliance lanes and required mechanics. These rules assist targeting/shortcuts; unattended clears remain unverified."));
                MaterialText.TextWrapped(Ui.T("Published presets arrive through Update JSON or the normal 24-hour refresh after publication. Downloading keeps the active selection."));
                MaterialText.TextWrapped(Ui.T("Published presets are protected and updated automatically. Use New Preset to create a personal copy."));
                break;
        }
    }

    private static void DrawExplorerGuide()
    {
        DrawSection("Filters and selection");
        MaterialText.TextWrapped(Ui.T("name / base id / object kind; | for OR"));
        MaterialText.TextWrapped(Ui.T("Best-effort layer filter. Rows with only off-layer scoped rule hits are hidden; rows with no layer evidence stay visible."));
        MaterialText.TextWrapped(Ui.T("Click a marker or row to pin it."));
        MaterialText.TextWrapped(Ui.T("Click again to cycle overlapping markers."));
        MaterialText.TextWrapped(Ui.T("Drag the divider to resize the map; double-click to reset."));
        DrawSection("Distances and rule matches");
        MaterialText.BulletText(Ui.T("3D distance from you, in yalms."));
        MaterialText.BulletText(Ui.T("Absolute height difference, in yalms."));
        MaterialText.BulletText(Ui.T("Rule matches before layer filtering."));
        DrawSection("Create a first rule");
        MaterialText.TextWrapped(Ui.T("Object Explorer -> RULE -> choose Class -> fill the relevant colored fields -> save an active custom preset -> retest from a clean enough state."));
        MaterialText.TextWrapped(Ui.T("When enabled, RULE seeds object XYZ coordinates plus a 6y radius. BaseId remains 0; observed BaseId is kept in Notes."));
        DrawSection("Coordinates and map flags");
        MaterialText.TextWrapped(Ui.T("Map coordinates use X,Z: `11.3,10.4`. Ordinary/manual world coordinates use X,Y,Z: `154.1,101.9,-34.2`. Cardinal holds use world X,Z: `123.4,-56.7`; X,Y,Z is accepted and Y is ignored."));
        MaterialText.BulletText(Ui.T("Place a map flag at the object."));
        MaterialText.BulletText(Ui.T("Copy the object's XYZ coordinates."));
        DrawSection("Exports and explicit actions");
        MaterialText.TextWrapped(Ui.T("Exports all buffered server events and every loaded object-table entry, independent of viewer filters."));
        MaterialText.BulletText(Ui.T("Move to the object's position."));
        MaterialText.BulletText(Ui.T("Fly to the object's position."));
        MaterialText.TextWrapped(Ui.T("Manual teleport only. DNav readiness, territory, filters and shared cooldown still apply."));
    }

    private static void DrawFieldList(RuleClassificationSemantics semantics, RuleFieldUse use, int column)
    {
        ImGui.TableSetColumnIndex(column);
        foreach (var field in semantics.Fields.Where(x => x.Value == use).Select(x => x.Key))
            MaterialText.BulletText(field);
    }

    private static void DrawSection(string text)
    {
        MaterialSettings.Section(Ui.T(text));
    }
}
