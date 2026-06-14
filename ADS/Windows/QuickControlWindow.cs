using System.Numerics;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class QuickControlWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;

    public QuickControlWindow(Plugin plugin)
        : base("ADS Controls###ADSQuickControls")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400f, 220f),
            MaximumSize = new Vector2(620f, 620f),
        };
        Size = new Vector2(460f, 300f);
    }

    public void Dispose()
    {
    }

    public override void OnClose()
    {
        plugin.DebugStrafeService.Release("mini close");
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        DrawStatusSummary();
        ImGui.Spacing();
        DrawPrimaryActions();

        if (ImGui.CollapsingHeader("Tools", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawToolShortcuts();
        }

        if (plugin.DebugStrafeService.Enabled
            && ImGui.CollapsingHeader("Debug Strafe"))
        {
            DrawDebugStrafeControls();
        }

        if (ImGui.CollapsingHeader("Details"))
            DrawDetails();
    }

    private void DrawStatusSummary()
    {
        ImGui.TextUnformatted($"{plugin.ExecutionService.CurrentMode} / {plugin.ExecutionService.CurrentPhase}");
        ImGui.TextWrapped(plugin.ExecutionService.LastStatus);
    }

    private void DrawPrimaryActions()
    {
        var canStartInside = plugin.DutyContextService.Current.InInstancedDuty;
        if (!ImGui.BeginTable("ADSQuickPrimaryActions", 3, ImGuiTableFlags.SizingStretchSame))
            return;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (ImGui.Button("Start Outside", new Vector2(-1f, 30f)))
            plugin.StartDutyFromOutside();

        ImGui.TableSetColumnIndex(1);
        ImGui.BeginDisabled(!canStartInside);
        if (ImGui.Button("Start Inside", new Vector2(-1f, 30f)))
            plugin.StartDutyFromInside();
        ImGui.EndDisabled();

        ImGui.TableSetColumnIndex(2);
        ImGui.BeginDisabled(!canStartInside);
        if (ImGui.Button("Resume", new Vector2(-1f, 30f)))
            plugin.ResumeDutyFromInside();
        ImGui.EndDisabled();

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.BeginDisabled(!plugin.ExecutionService.IsOwned);
        if (ImGui.Button("Leave", new Vector2(-1f, 30f)))
            plugin.LeaveDuty();
        ImGui.EndDisabled();

        ImGui.TableSetColumnIndex(1);
        if (ImGui.Button("Stop", new Vector2(-1f, 30f)))
            plugin.StopOwnership();

        ImGui.EndTable();
    }

    private void DrawToolShortcuts()
    {
        if (!ImGui.BeginTable("ADSQuickToolShortcuts", 3, ImGuiTableFlags.SizingStretchSame))
            return;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (ImGui.Button("Loot", new Vector2(-1f, 28f)))
            plugin.OpenLootUi();
        ImGui.TableSetColumnIndex(1);
        if (ImGui.Button("Rules", new Vector2(-1f, 28f)))
            plugin.OpenRuleEditorUi();
        ImGui.TableSetColumnIndex(2);
        if (ImGui.Button("Objects", new Vector2(-1f, 28f)))
            plugin.OpenObjectExplorerUi();

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (ImGui.Button("Dialogs", new Vector2(-1f, 28f)))
            plugin.OpenDialogRuleEditorUi();
        ImGui.TableSetColumnIndex(1);
        ImGui.BeginDisabled(plugin.RemoteJsonUpdateService.IsUpdateRunning);
        if (ImGui.Button("Update", new Vector2(-1f, 28f)))
            plugin.ForceRemoteJsonUpdate();
        ImGui.EndDisabled();
        ImGui.TableSetColumnIndex(2);
        if (ImGui.Button("Disable QSTcomp", new Vector2(-1f, 28f)))
            plugin.DisableQstCompanion();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(QstCompanionWarningService.DisableCommand);
        ImGui.EndTable();
    }

    private void DrawDebugStrafeControls()
    {
        var leftLabel = plugin.DebugStrafeService.IsHoldingLeft ? "Release Left" : "Strafe Left";
        if (ImGui.Button(leftLabel, new Vector2(140f, 28f)))
            plugin.ToggleDebugStrafeLeft();
        ImGui.SameLine();
        var rightLabel = plugin.DebugStrafeService.IsHoldingRight ? "Release Right" : "Strafe Right";
        if (ImGui.Button(rightLabel, new Vector2(140f, 28f)))
            plugin.ToggleDebugStrafeRight();
        ImGui.TextWrapped(plugin.DebugStrafeService.Status);
    }

    private void DrawDetails()
    {
        var context = plugin.DutyContextService.Current;
        var planner = plugin.ObjectivePlannerService.Current;
        var duty = context.CurrentDuty?.EnglishName ?? (context.InInstancedDuty ? $"Territory {context.TerritoryTypeId}" : "No duty");
        ImGui.TextWrapped($"Duty: {duty}");
        ImGui.TextWrapped($"Objective: {planner.Objective}");
        DrawTreasureFollowSummary();

        if (plugin.InnEntryService.IsRunning)
            ImGui.TextWrapped($"Inn: {plugin.InnEntryService.StatusMessage}");
        if (plugin.UtilityAutomationService.IsRunning)
            ImGui.TextWrapped($"Utility: {plugin.UtilityAutomationService.StatusMessage}");
    }

    private void DrawTreasureFollowSummary()
    {
        var target = plugin.TreasurePortalOpenerTracker.Current;
        var follow = plugin.BossModMultiboxFollowService;
        var targetName = string.IsNullOrWhiteSpace(target?.OpenerName) ? "-" : target.OpenerName;
        var targetLocality = target is null ? "-" : target.IsLocalOpener ? "local" : "remote";
        var commandAccepted = follow.BmraiFollowCommandAccepted is null
            ? "not sent"
            : follow.BmraiFollowCommandAccepted.Value ? "accepted" : "rejected";

        ImGui.TextWrapped($"Treasure: {plugin.ExecutionService.TreasureDungeonRoleDisplayName} | opener {targetName} ({targetLocality})");
        ImGui.TextWrapped($"Follow: {follow.BmraiFollowCommandMethod} {commandAccepted} | {follow.BmraiFollowCommandStatus}");
    }
}
