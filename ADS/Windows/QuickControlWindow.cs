using ADS.Localization;
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

        if (ImGui.CollapsingHeader(Ui.L("Tools"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawToolShortcuts();
        }

        if (plugin.DebugStrafeService.Enabled
            && ImGui.CollapsingHeader(Ui.L("Debug Strafe")))
        {
            DrawDebugStrafeControls();
        }

        if (ImGui.CollapsingHeader(Ui.L("Details")))
            DrawDetails();
    }

    private void DrawStatusSummary()
    {
        ImGui.TextUnformatted(Ui.T("{0} / {1}", Ui.Display(plugin.ExecutionService.CurrentMode.ToString()), Ui.Display(plugin.ExecutionService.CurrentPhase.ToString())));
        ImGui.TextWrapped(Ui.Display(plugin.ExecutionService.LastStatus));
    }

    private void DrawPrimaryActions()
    {
        var inInstancedDuty = plugin.DutyContextService.Current.InInstancedDuty;
        if (!ImGui.BeginTable("ADSQuickPrimaryActions", 3, ImGuiTableFlags.SizingStretchSame))
            return;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (ImGui.Button(Ui.L("Start Outside"), new Vector2(-1f, 30f)))
            plugin.StartDutyFromOutside();

        ImGui.TableSetColumnIndex(1);
        ImGui.BeginDisabled(!inInstancedDuty);
        if (ImGui.Button(Ui.L("Start Inside"), new Vector2(-1f, 30f)))
            plugin.StartDutyFromInside();
        ImGui.EndDisabled();

        ImGui.TableSetColumnIndex(2);
        ImGui.BeginDisabled(!inInstancedDuty);
        if (ImGui.Button(Ui.L("Resume"), new Vector2(-1f, 30f)))
            plugin.ResumeDutyFromInside();
        ImGui.EndDisabled();

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.BeginDisabled(!inInstancedDuty);
        if (ImGui.Button(Ui.L("Leave"), new Vector2(-1f, 30f)))
            plugin.LeaveDuty();
        ImGui.EndDisabled();

        ImGui.TableSetColumnIndex(1);
        if (ImGui.Button(Ui.L("Stop"), new Vector2(-1f, 30f)))
            plugin.StopOwnership();

        ImGui.EndTable();
    }

    private void DrawToolShortcuts()
    {
        if (!ImGui.BeginTable("ADSQuickToolShortcuts", 3, ImGuiTableFlags.SizingStretchSame))
            return;

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (ImGui.Button(Ui.L("Loot"), new Vector2(-1f, 28f)))
            plugin.OpenLootUi();
        ImGui.TableSetColumnIndex(1);
        if (ImGui.Button(Ui.L("Rules"), new Vector2(-1f, 28f)))
            plugin.OpenRuleEditorUi();
        ImGui.TableSetColumnIndex(2);
        if (ImGui.Button(Ui.L("Objects"), new Vector2(-1f, 28f)))
            plugin.OpenObjectExplorerUi();

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (ImGui.Button(Ui.L("Dialogs"), new Vector2(-1f, 28f)))
            plugin.OpenDialogRuleEditorUi();
        ImGui.TableSetColumnIndex(1);
        ImGui.BeginDisabled(plugin.RemoteJsonUpdateService.IsUpdateRunning);
        if (ImGui.Button(Ui.L("Update"), new Vector2(-1f, 28f)))
            plugin.ForceRemoteJsonUpdate();
        ImGui.EndDisabled();
        ImGui.TableSetColumnIndex(2);
        if (ImGui.Button(Ui.L("Shop Lists"), new Vector2(-1f, 28f)))
            plugin.OpenShopListsUi();
        ImGui.EndTable();

        if (!ImGui.BeginTable("ADSQuickCompanionControls", 2, ImGuiTableFlags.SizingStretchSame))
            return;
        DrawCompanionControls("QSTcomp", QstCompanionWarningService.InternalName);
        DrawCompanionControls("HealBot", "Coppelia");
        ImGui.EndTable();
        if (ImGui.Button(Ui.L("Reset RSR Healing"), new Vector2(-1f, 28f)))
            plugin.ResetRsrHealing();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Ui.T("Set RSR to Off and restore Coppelia's eleven healing defaults. Works without HealBot loaded."));
    }

    private void DrawCompanionControls(string label, string internalName)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (ImGui.Button(Ui.L("Enable {0}", label), new Vector2(-1f, 28f)))
            plugin.SetCompanionPluginEnabled(internalName, label, true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Ui.T("/xlenableplugin {0}", internalName));
        ImGui.TableSetColumnIndex(1);
        if (ImGui.Button(Ui.L("Disable {0}", label), new Vector2(-1f, 28f)))
            plugin.SetCompanionPluginEnabled(internalName, label, false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Ui.T("/xldisableplugin {0}", internalName));
    }

    private void DrawDebugStrafeControls()
    {
        var leftLabel = plugin.DebugStrafeService.IsHoldingLeft ? "Release Left" : "Strafe Left";
        if (ImGui.Button(Ui.L(leftLabel), new Vector2(140f, 28f)))
            plugin.ToggleDebugStrafeLeft();
        ImGui.SameLine();
        var rightLabel = plugin.DebugStrafeService.IsHoldingRight ? "Release Right" : "Strafe Right";
        if (ImGui.Button(Ui.L(rightLabel), new Vector2(140f, 28f)))
            plugin.ToggleDebugStrafeRight();
        ImGui.TextWrapped(Ui.Display(plugin.DebugStrafeService.Status));
    }

    private void DrawDetails()
    {
        var context = plugin.DutyContextService.Current;
        var planner = plugin.ObjectivePlannerService.Current;
        var duty = context.CurrentDuty is { } currentDuty
            ? Ui.DutyName(currentDuty)
            : context.InInstancedDuty ? Ui.T("Territory {0}", context.TerritoryTypeId) : Ui.T("No duty");
        ImGui.TextWrapped(Ui.T("Duty: {0}", duty));
        ImGui.TextWrapped(Ui.T("Objective: {0}", Ui.Display(planner.Objective)));
        DrawTreasureFollowSummary();

        if (plugin.InnEntryService.IsRunning)
            ImGui.TextWrapped(Ui.T("Inn: {0}", Ui.Display(plugin.InnEntryService.StatusMessage)));
        if (plugin.UtilityAutomationService.IsRunning)
            ImGui.TextWrapped(Ui.T("Utility: {0}", Ui.Display(plugin.UtilityAutomationService.StatusMessage)));
    }

    private void DrawTreasureFollowSummary()
    {
        var target = plugin.TreasurePortalOpenerTracker.Current;
        var follow = plugin.BossModMultiboxFollowService;
        var targetName = string.IsNullOrWhiteSpace(target?.OpenerName) ? "-" : target.OpenerName;
        var targetLocality = target is null ? "-" : Ui.T(target.IsLocalOpener ? "local" : "remote");
        var commandAccepted = follow.BmraiFollowCommandAccepted is null
            ? Ui.T("not sent")
            : Ui.T(follow.BmraiFollowCommandAccepted.Value ? "accepted" : "rejected");

        ImGui.TextWrapped(Ui.T("Treasure: {0} | opener {1} ({2})", Ui.Display(plugin.ExecutionService.TreasureDungeonRoleDisplayName), targetName, targetLocality));
        ImGui.TextWrapped(Ui.T("Follow: {0} {1} | {2}", follow.BmraiFollowCommandMethod, commandAccepted, Ui.Display(follow.BmraiFollowCommandStatus)));
    }
}
