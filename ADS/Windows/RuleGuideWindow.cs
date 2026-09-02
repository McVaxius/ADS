using System.Numerics;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class RuleGuideWindow : PositionedWindow, IDisposable
{
    public RuleGuideWindow()
        : base("ADS Rule Guide###ADSRuleGuide")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760f, 520f),
            MaximumSize = new Vector2(1800f, 1800f),
        };
        Size = new Vector2(1050f, 850f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        DrawSection("Quick Start");
        ImGui.TextWrapped("Object Explorer -> RULE -> choose Class -> fill the relevant colored fields -> save DEFAULT -> retest from a clean enough state.");
        ImGui.TextWrapped("Field cues: red = required, amber = recommended, normal = optional, dim = ignored by this class. Class selection and cues never clear ignored stored values.");

        DrawSection("How A Rule Wins");
        ImGui.BulletText("1. Scope: Duty, Terr, CFC, then Layer.");
        ImGui.BulletText("2. Object match: Kind, BaseId, Name/Match, then optional positional selector.");
        ImGui.BulletText("3. Gates: candidates failing Dist or Y are removed before a winner is selected.");
        ImGui.BulletText("4. Priority: lower Pri wins among eligible matching candidates.");
        ImGui.BulletText("5. Behavior and timing: Class, Wait-before, and Wait-after control execution.");
        ImGui.TextWrapped("A failed higher-ranked candidate cannot shadow a lower eligible rule. Failed Required/BossFight/CombatFriendly BattleNpc rules are non-blocking; failed Ignored/Follow BattleNpc rules keep generic monster fallback.");

        DrawSection("Choose Class By Goal");
        if (ImGui.BeginTable("ADSRuleGuideGoals", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthFixed, 180f);
            ImGui.TableSetupColumn("Use When", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Common Example", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            foreach (var semantics in RuleSemanticsCatalog.Classifications)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(semantics.Label);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextWrapped(semantics.Goal);
                ImGui.TableSetColumnIndex(2);
                ImGui.TextWrapped(semantics.CommonExample);
            }
            ImGui.EndTable();
        }

        DrawSection("Class Help And Field Matrix");
        ImGui.TextWrapped("Use the ? button beside a row's Class for focused help. Expand a class below for full required/recommended/optional/ignored guidance.");
        foreach (var semantics in RuleSemanticsCatalog.Classifications)
        {
            if (!ImGui.CollapsingHeader(semantics.Label))
                continue;

            ImGui.TextWrapped(semantics.Behavior);
            ImGui.TextWrapped($"Relevant editor fields: {string.Join(", ", RuleSemanticsCatalog.GetRelevantEditorFieldLabels(semantics))}");
            if (ImGui.BeginTable($"ADSRuleGuideMatrix{semantics.Value}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Required", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Recommended", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Optional", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Ignored", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
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
            ImGui.TableSetupColumn("JSON Field", ImGuiTableColumnFlags.WidthFixed, 240f);
            ImGui.TableSetupColumn("Meaning", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            foreach (var field in RuleSemanticsCatalog.UniversalFields)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(field);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextWrapped(RuleSemanticsCatalog.FieldGlossary[field]);
            }
            ImGui.EndTable();
        }

        DrawSection("Coordinate And JSON Examples");
        ImGui.TextWrapped("Map coordinates use X,Z: `11.3,10.4`. Ordinary/manual world coordinates use X,Y,Z: `154.1,101.9,-34.2`. Cardinal holds use world X,Z: `123.4,-56.7`; X,Y,Z is accepted and Y is ignored.");
        ImGui.TextUnformatted("""
{
  "classification": "CardinalHoldNorth",
  "worldCoordinates": "123.4,-56.7",
  "maxDistance": 3.0,
  "waitAtDestinationSeconds": 1.5,
  "priority": 100
}
""");
        ImGui.TextWrapped("A cardinal hold activates only while ADS owns duty execution and the player is inside its X/Z radius. ADS stops vnav, holds direct movement for the full duration, releases input, then ghosts the row. Interrupted holds remain unconsumed.");
    }

    private static void DrawFieldList(RuleClassificationSemantics semantics, RuleFieldUse use, int column)
    {
        ImGui.TableSetColumnIndex(column);
        foreach (var field in semantics.Fields.Where(x => x.Value == use).Select(x => x.Key))
            ImGui.BulletText(field);
    }

    private static void DrawSection(string text)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(text);
        ImGui.Separator();
    }
}
