using System.Globalization;
using System.Numerics;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class ReflectionWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;

    public ReflectionWindow(Plugin plugin)
        : base("ADS Reflection Controls###ADSReflection")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520f, 360f),
            MaximumSize = new Vector2(1800f, 1400f),
        };
        Size = new Vector2(760f, 560f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        var status = plugin.BmrReflectionService.Status;
        DrawStatus(status);
        ImGui.Separator();
        DrawControls(status);
    }

    private void DrawStatus(BmrReflectionStatus status)
    {
        ImGui.TextUnformatted("BossMod Reborn");
        ImGui.TextUnformatted($"Installed: {(status.BmrInstalled ? "YES" : "NO")}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Loaded: {(status.BmrLoaded ? "YES" : "NO")}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Ready: {(status.ReflectionReady ? "YES" : "NO")}");

        var name = string.IsNullOrWhiteSpace(status.BmrName) ? "(not found)" : status.BmrName;
        var internalName = string.IsNullOrWhiteSpace(status.BmrInternalName) ? "-" : status.BmrInternalName;
        ImGui.TextUnformatted($"Plugin: {name} / {internalName} / {status.BmrVersion ?? "-"}");
        ImGui.TextWrapped($"Reflection state: {status.ReflectionState}");
        if (!string.IsNullOrWhiteSpace(status.Error))
            ImGui.TextWrapped($"Error: {status.Error}");

        ImGui.Spacing();
        ImGui.TextUnformatted($"Queen disabled: desired={(status.QueenDesiredDisabled ? "YES" : "NO")} actual={(status.QueenActuallyDisabled ? "YES" : "NO")}");
        ImGui.TextUnformatted($"Hunts disabled: desired={(status.HuntsDesiredDisabled ? "YES" : "NO")} actual={(status.HuntsActuallyDisabled ? "YES" : "NO")}");
        ImGui.TextUnformatted($"Registry: disabled={status.DisabledRegistryEntryCount} huntsKnown={status.KnownHuntModuleCount} total={status.RegisteredModuleCount}");
        ImGui.TextUnformatted($"Live removals last update: {status.RemovedLiveModuleCount}");

        var currentMax = status.CurrentMaxLoadDistance?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-";
        var capturedMax = status.CapturedMaxLoadDistance?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-";
        ImGui.TextUnformatted($"MaxLoadDistance: current={currentMax} minimized={status.MinimizedMaxLoadDistance.ToString("0.###", CultureInfo.InvariantCulture)} capturedReset={capturedMax}");
        ImGui.TextWrapped($"Last action: {status.LastAction}");
    }

    private void DrawControls(BmrReflectionStatus status)
    {
        var enabled = plugin.Configuration.ReflectionToolsEnabled;
        if (ImGui.Checkbox("Enable BMR reflection tools", ref enabled))
            plugin.BmrReflectionService.SetToolsEnabled(enabled);

        ImGui.Spacing();
        if (ImGui.Button(status.QueenDesiredDisabled ? "Enable Queen Lunatender" : "Disable Queen Lunatender"))
            plugin.BmrReflectionService.RequestQueenLunatenderDisabled(!status.QueenDesiredDisabled);
        ImGui.SameLine();
        ImGui.TextDisabled($"OID 0x{BmrReflectionService.QueenLunatenderOid:X}");

        if (ImGui.Button(status.HuntsDesiredDisabled ? "Enable Hunt Modules" : "Disable Hunt Modules"))
            plugin.BmrReflectionService.RequestHuntsDisabled(!status.HuntsDesiredDisabled);

        ImGui.Spacing();
        var minimizedValue = plugin.Configuration.ReflectionMinimizedMaxLoadDistance;
        if (ImGui.InputFloat("Minimized MaxLoadDistance", ref minimizedValue, 1f, 10f, "%.1f"))
        {
            if (!float.IsFinite(minimizedValue) || minimizedValue <= 0f)
                minimizedValue = BmrReflectionService.DefaultMinimizedMaxLoadDistance;
            plugin.Configuration.ReflectionMinimizedMaxLoadDistance = Math.Clamp(minimizedValue, 0.1f, BmrReflectionService.DefaultFallbackMaxLoadDistance);
            plugin.SaveConfiguration();
        }

        if (ImGui.Button("Minimize MaxLoadDistance"))
            plugin.BmrReflectionService.RequestMinimizeMaxLoadDistance();
        ImGui.SameLine();
        if (ImGui.Button("Reset MaxLoadDistance"))
            plugin.BmrReflectionService.RequestResetMaxLoadDistance();

        ImGui.Spacing();
        if (ImGui.SmallButton("Copy Reflection Status JSON"))
            ImGui.SetClipboardText(plugin.BmrReflectionService.GetStatusJson());
    }
}
