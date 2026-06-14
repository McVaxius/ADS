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
        ImGui.TextWrapped("Read-only rule semantics. Scope filters first; lower Priority wins among matching candidates. DutyEnglishName, TerritoryTypeId, ContentFinderConditionId, and Layer narrow a row. Zero/blank scope fields are wildcards.");
        DrawSection("Universal Field Glossary");
        if (ImGui.BeginTable("ADSRuleGuideGlossary", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 240f);
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

        DrawSection("Coordinates");
        ImGui.TextWrapped("Map coordinates use X,Z: `11.3,10.4`. Ordinary/manual world coordinates use X,Y,Z: `154.1,101.9,-34.2`. Cardinal holds use world X,Z: `123.4,-56.7`; X,Y,Z is accepted and Y is ignored.");
        DrawSection("Classifications And Field Matrix");
        foreach (var semantics in RuleSemanticsCatalog.Classifications)
        {
            if (!ImGui.CollapsingHeader(semantics.Label))
                continue;

            ImGui.TextWrapped(semantics.Behavior);
            if (ImGui.BeginTable($"ADSRuleGuideMatrix{semantics.Value}", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Required", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Optional", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Ignored", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                ImGui.TableNextRow();
                DrawFieldList(semantics, RuleFieldUse.Required, 0);
                DrawFieldList(semantics, RuleFieldUse.Optional, 1);
                DrawFieldList(semantics, RuleFieldUse.Ignored, 2);
                ImGui.EndTable();
            }
        }

        DrawSection("Cardinal Hold Example");
        ImGui.TextUnformatted("""
{
  "classification": "CardinalHoldNorth",
  "worldCoordinates": "123.4,-56.7",
  "maxDistance": 3.0,
  "waitAtDestinationSeconds": 1.5,
  "priority": 100
}
""");
        ImGui.TextWrapped("A cardinal hold activates only while ADS owns duty execution and the player is inside its X/Z radius. ADS stops vnav, holds direct movement for the full duration, releases input, then ghosts the row. Interrupted holds remain unconsumed. Cardinal ghosts reset on duty reset or a stable in-duty relocation of at least 40y; that recovery clears only cardinal ghosts.");
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
