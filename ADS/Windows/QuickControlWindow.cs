using ADS.Localization;
using AethertekUI;
using System.Numerics;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
            MinimumSize = new Vector2(300f, 420f),
            MaximumSize = new Vector2(1200f, 1600f),
        };
        Size = plugin.Configuration.UiCompact ? new Vector2(376f, 620f) : new Vector2(376f, 968f);
        Flags |= ImGuiWindowFlags.HorizontalScrollbar;
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Home, Priority = 0, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.OpenMainUi(); },
            ShowTooltip = () => MaterialText.SetTooltip(Ui.T("Main")),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Cog, Priority = -10, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.OpenConfigUi(); },
            ShowTooltip = () => MaterialText.SetTooltip(Ui.T("Settings")),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Play, Priority = -20, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left && plugin.GetDutyUiActionBlocker("Start Outside") == null) plugin.StartDutyFromOutside(); },
            ShowTooltip = () => ShowDutyTitleTooltip("Start Outside"),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.SignInAlt, Priority = -30, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left && plugin.GetDutyUiActionBlocker("Start Inside") == null) plugin.StartDutyFromInside(); },
            ShowTooltip = () => ShowDutyTitleTooltip("Start Inside"),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Redo, Priority = -40, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left && plugin.GetDutyUiActionBlocker("Resume") == null) plugin.ResumeDutyFromInside(); },
            ShowTooltip = () => ShowDutyTitleTooltip("Resume"),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Stop, Priority = -50, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.StopOwnership(); },
            ShowTooltip = () => ShowDutyTitleTooltip("Stop"),
        });
    }

    public void Dispose()
    {
    }

    public override void PreDraw()
    {
        PrepareWindowPlacement();
        WindowName = $"{Ui.T("ADS Controls")}###ADSQuickControls";
        ReserveTitleSpace(300);
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
    {
        base.PostDraw();
        WindowLayout.PaintTitleWithImage(this);
    }

    private void ShowDutyTitleTooltip(string action)
    {
        var blocker = plugin.GetDutyUiActionBlocker(action);
        MaterialText.SetTooltip(Ui.T(action) + (blocker == null ? string.Empty : "\n" + Ui.Display(blocker))
            + "\n" + Ui.Display(plugin.ExecutionService.LastStatus));
    }

    public override void OnClose()
    {
        plugin.DebugStrafeService.Release("mini close");
    }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        FinalizePendingWindowPlacement();

        DrawStatusSummary();
        ImGui.Spacing();
        DrawPrimaryActions();
        ImGui.Separator();
        ImGui.Spacing();

        if (WindowLayout.Header(Ui.L("Tools"), MaterialIcon.Wrench, ImGuiTreeNodeFlags.DefaultOpen))
        {
            AdsPresentation.ToolPanel(DrawToolShortcuts, ImGui.GetID(""));
        }
        ImGui.Spacing();

        if (plugin.DebugStrafeService.Enabled
            && WindowLayout.Header(Ui.L("Debug Strafe"), MaterialIcon.Sliders))
        {
            DrawDebugStrafeControls();
        }

        if (WindowLayout.Header(Ui.L("Details"), MaterialIcon.Info))
            DrawDetails();
    }

    private void DrawStatusSummary()
    {
        AdsPresentation.QuickStatus(plugin.Fonts.Push,
            Ui.T("{0} / {1}", Ui.Display(plugin.ExecutionService.CurrentMode.ToString()), Ui.Display(plugin.ExecutionService.CurrentPhase.ToString())),
            Ui.Display(plugin.ExecutionService.LastStatus));
    }

    private void DrawPrimaryActions()
    {
        var inInstancedDuty = plugin.DutyContextService.Current.InInstancedDuty;
        using var actionStyle = new MaterialStyleScope();
        actionStyle.Style(ImGuiStyleVar.CellPadding, new Vector2(0, 4) * MaterialTheme.Metrics.Scale);
        if (!ImGui.BeginTable("ADSQuickPrimaryActions", 1, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            ImGui.TableNextColumn();
            if (AdsPresentation.QuickAction(plugin.Fonts, Ui.T("Start Outside"), Ui.T("Prepare and start"), "Start Outside", MaterialTheme.Current.Colors.Primary))
                plugin.StartDutyFromOutside();
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(!inInstancedDuty);
            try
            {
                if (AdsPresentation.QuickAction(plugin.Fonts, Ui.T("Start Inside"), Ui.T("Start in current duty"), "Start Inside", MaterialTheme.Current.Colors.Primary))
                    plugin.StartDutyFromInside();
                ImGui.TableNextColumn();
                if (AdsPresentation.QuickAction(plugin.Fonts, Ui.T("Resume"), Ui.T("Continue execution"), "Resume")) plugin.ResumeDutyFromInside();
                ImGui.TableNextColumn();
                if (AdsPresentation.QuickAction(plugin.Fonts, Ui.T("Leave"), Ui.T("Leave current duty"), "Leave")) plugin.LeaveDuty();
            }
            finally { ImGui.EndDisabled(); }
            ImGui.TableNextColumn();
            if (AdsPresentation.QuickAction(plugin.Fonts, Ui.T("Stop"), Ui.T("Halt execution"), "Stop", AdsPresentation.Rgb(0xBE172A))) plugin.StopOwnership();
        }
        finally { ImGui.EndTable(); }
    }

    private void DrawToolShortcuts()
    {
        using var shortcutFont = plugin.Fonts.Push(UiFontRole.Caption);
        using var shortcutStyle = new MaterialStyleScope();
        shortcutStyle.Style(ImGuiStyleVar.FramePadding, new Vector2(AdsPresentation.Compact ? 6 : 10, AdsPresentation.Compact ? 2 : 7) * MaterialTheme.Metrics.Scale);
        shortcutStyle.Style(ImGuiStyleVar.CellPadding, new Vector2(4, AdsPresentation.Compact ? 2 : 4) * MaterialTheme.Metrics.Scale);
        shortcutStyle.Style(ImGuiStyleVar.ItemSpacing, new Vector2(8, 0) * MaterialTheme.Metrics.Scale);
        var columns = ToolColumns();
        if (!ImGui.BeginTable("ADSQuickToolShortcuts", columns, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            ImGui.TableNextColumn();
            if (WindowLayout.Button(Ui.L("Loot"), ShortcutSize, MaterialIcon.Chest)) plugin.OpenLootUi();
            ImGui.TableNextColumn();
            if (WindowLayout.Button(Ui.L("Rules"), ShortcutSize, MaterialIcon.List)) plugin.OpenRuleEditorUi();
            ImGui.TableNextColumn();
            if (WindowLayout.Button(Ui.L("Objects"), ShortcutSize, MaterialIcon.Cube)) plugin.OpenObjectExplorerUi();
            ImGui.TableNextColumn();
            if (WindowLayout.Button(Ui.L("Dialogs"), ShortcutSize, MaterialIcon.Chat)) plugin.OpenDialogRuleEditorUi();
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(plugin.RemoteJsonUpdateService.IsUpdateRunning);
            try { if (WindowLayout.Button(Ui.L("Update"), ShortcutSize, MaterialIcon.Refresh)) plugin.ForceRemoteJsonUpdate(); }
            finally { ImGui.EndDisabled(); }
            ImGui.TableNextColumn();
            if (WindowLayout.Button(Ui.L("Shop Lists"), ShortcutSize, MaterialIcon.Cart)) plugin.OpenShopListsUi();
        }
        finally { ImGui.EndTable(); }

        if (!ImGui.BeginTable("ADSQuickCompanionControls", columns, ImGuiTableFlags.SizingStretchSame))
            return;
        DrawCompanionControls("QSTcomp", QstCompanionWarningService.InternalName, columns);
        DrawCompanionControls("HealBot", "Coppelia", columns);
        DrawCompanionControls("AutoDuty", "AutoDuty", columns);
        ImGui.EndTable();
        if (WindowLayout.Button(Ui.L("Reset RSR Healing"), ShortcutSize, MaterialIcon.Refresh))
            plugin.ResetRsrHealing();
        if (ImGui.IsItemHovered())
            MaterialText.SetTooltip(Ui.T("Set RSR to Off and restore Coppelia's eleven healing defaults. Works without HealBot loaded."));
    }

    private static Vector2 ShortcutSize => new(-1f, (AdsPresentation.Compact ? 26 : 46) * MaterialTheme.Metrics.Scale);

    private static int ToolColumns()
    {
        var labels = new[] { Ui.L("Loot"), Ui.L("Rules"), Ui.L("Objects"), Ui.L("Dialogs"), Ui.L("Update"), Ui.L("Shop Lists"),
            Ui.L("Enable {0}", "QSTcomp"), Ui.L("Disable {0}", "QSTcomp"), Ui.L("Enable {0}", "HealBot"), Ui.L("Disable {0}", "HealBot"),
            Ui.L("Enable {0}", "AutoDuty"), Ui.L("Disable {0}", "AutoDuty") };
        var width = labels.Max(label => WindowLayout.ButtonMinimum(label, MaterialIcon.Play)) + ImGui.GetStyle().CellPadding.X * 2;
        return Math.Clamp((int)(ImGui.GetContentRegionAvail().X / width), 1, 2);
    }

    private void DrawCompanionControls(string label, string internalName, int columns)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (WindowLayout.Button(Ui.L("Enable {0}", label), ShortcutSize, label == "HealBot" ? MaterialIcon.Plus : MaterialIcon.Play))
            plugin.SetCompanionPluginEnabled(internalName, label, true);
        if (ImGui.IsItemHovered())
            MaterialText.SetTooltip(Ui.T("/xlenableplugin {0}", internalName));
        if (columns == 1) ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(columns == 1 ? 0 : 1);
        if (WindowLayout.Button(Ui.L("Disable {0}", label), ShortcutSize, MaterialIcon.Pause))
            plugin.SetCompanionPluginEnabled(internalName, label, false);
        if (ImGui.IsItemHovered())
            MaterialText.SetTooltip(Ui.T("/xldisableplugin {0}", internalName));
    }

    private void DrawDebugStrafeControls()
    {
        var leftLabel = plugin.DebugStrafeService.IsHoldingLeft ? "Release Left" : "Strafe Left";
        if (WindowLayout.Button(Ui.L(leftLabel), new Vector2(140f, 28f)))
            plugin.ToggleDebugStrafeLeft();
        ImGui.SameLine();
        var rightLabel = plugin.DebugStrafeService.IsHoldingRight ? "Release Right" : "Strafe Right";
        if (WindowLayout.Button(Ui.L(rightLabel), new Vector2(140f, 28f)))
            plugin.ToggleDebugStrafeRight();
        MaterialText.TextWrapped(Ui.Display(plugin.DebugStrafeService.Status));
    }

    private void DrawDetails()
    {
        var context = plugin.DutyContextService.Current;
        var planner = plugin.ObjectivePlannerService.Current;
        var duty = context.CurrentDuty is { } currentDuty
            ? Ui.DutyName(currentDuty)
            : context.InInstancedDuty ? Ui.T("Territory {0}", context.TerritoryTypeId) : Ui.T("No duty");
        MaterialText.TextWrapped(Ui.T("Duty: {0}", duty));
        MaterialText.TextWrapped(Ui.T("Objective: {0}", Ui.Display(planner.Objective)));
        DrawTreasureFollowSummary();

        if (plugin.InnEntryService.IsRunning)
            MaterialText.TextWrapped(Ui.T("Inn: {0}", Ui.Display(plugin.InnEntryService.StatusMessage)));
        if (plugin.UtilityAutomationService.IsRunning)
            MaterialText.TextWrapped(Ui.T("Utility: {0}", Ui.Display(plugin.UtilityAutomationService.StatusMessage)));
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

        MaterialText.TextWrapped(Ui.T("Treasure: {0} | opener {1} ({2})", Ui.Display(plugin.ExecutionService.TreasureDungeonRoleDisplayName), targetName, targetLocality));
        MaterialText.TextWrapped(Ui.T("Follow: {0} {1} | {2}", follow.BmraiFollowCommandMethod, commandAccepted, Ui.Display(follow.BmraiFollowCommandStatus)));
    }
}
