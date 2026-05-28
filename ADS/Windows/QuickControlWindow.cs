using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class QuickControlWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;

    public QuickControlWindow(Plugin plugin)
        : base("ADS Controls###ADSQuickControls", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360f, 120f),
            MaximumSize = new Vector2(560f, 340f),
        };
        Size = new Vector2(440f, 230f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        if (ImGui.Button("OUT-S", new Vector2(58f, 26f)))
            plugin.StartDutyFromOutside();
        ImGui.SameLine();
        if (ImGui.Button("IN-S", new Vector2(58f, 26f)))
            plugin.StartDutyFromInside();
        ImGui.SameLine();
        if (ImGui.Button("Leave", new Vector2(58f, 26f)))
            plugin.LeaveDuty();
        ImGui.SameLine();
        if (ImGui.Button("STOP", new Vector2(58f, 26f)))
            plugin.StopOwnership();

        if (ImGui.Button("Rules", new Vector2(58f, 26f)))
            plugin.OpenRuleEditorUi();
        ImGui.SameLine();
        if (ImGui.Button("Object", new Vector2(58f, 26f)))
            plugin.OpenObjectExplorerUi();
        ImGui.SameLine();
        if (ImGui.Button("Dialog", new Vector2(76f, 26f)))
            plugin.OpenDialogRuleEditorUi();
        ImGui.SameLine();
        if (plugin.RemoteJsonUpdateService.IsUpdateRunning)
            ImGui.BeginDisabled();
        if (ImGui.Button("UPDATE", new Vector2(76f, 26f)))
            plugin.ForceRemoteJsonUpdate();
        if (plugin.RemoteJsonUpdateService.IsUpdateRunning)
            ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextUnformatted($"{plugin.ExecutionService.CurrentMode} / {plugin.ExecutionService.CurrentPhase}");
        var duty = plugin.DutyContextService.Current.CurrentDuty?.EnglishName ?? "No duty";
        ImGui.TextUnformatted(duty);
        DrawTreasureFollowSummary();
        ImGui.TextWrapped(plugin.ExecutionService.LastStatus);

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
        var targetSource = string.IsNullOrWhiteSpace(target?.Source) ? "-" : target.Source;
        var targetLocality = target is null ? "-" : target.IsLocalOpener ? "local" : "remote";
        var commandAccepted = follow.BmraiFollowCommandAccepted is null
            ? "not sent"
            : follow.BmraiFollowCommandAccepted.Value ? "accepted" : "rejected";

        ImGui.TextWrapped($"Treasure role: {plugin.ExecutionService.TreasureDungeonRoleDisplayName} ({plugin.ExecutionService.TreasureDungeonRoleSource})");
        ImGui.TextWrapped($"Opener: {targetName} {targetLocality} src {targetSource}");
        ImGui.TextWrapped($"BMRAI/VBM: {follow.BmraiFollowCommandMethod} {commandAccepted} {follow.BmraiFollowCommandText}");
        ImGui.TextWrapped($"Reason: {follow.BmraiFollowCommandStatus}");
    }
}
