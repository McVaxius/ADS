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
            MinimumSize = new Vector2(250f, 120f),
            MaximumSize = new Vector2(420f, 240f),
        };
        Size = new Vector2(300f, 150f);
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

        ImGui.Separator();
        ImGui.TextUnformatted($"{plugin.ExecutionService.CurrentMode} / {plugin.ExecutionService.CurrentPhase}");
        var duty = plugin.DutyContextService.Current.CurrentDuty?.EnglishName ?? "No duty";
        ImGui.TextUnformatted(duty);
        ImGui.TextWrapped(plugin.ExecutionService.LastStatus);

        if (plugin.InnEntryService.IsRunning)
            ImGui.TextWrapped($"Inn: {plugin.InnEntryService.StatusMessage}");
        if (plugin.UtilityAutomationService.IsRunning)
            ImGui.TextWrapped($"Utility: {plugin.UtilityAutomationService.StatusMessage}");
    }
}
