using ADS.Localization;
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
        ImGui.TextUnformatted(Ui.T("BossMod Reborn"));
        ImGui.TextUnformatted(Ui.T("Installed: {0}", Ui.T((status.BmrInstalled ? "YES" : "NO"))));
        ImGui.SameLine();
        ImGui.TextUnformatted(Ui.T("Loaded: {0}", Ui.T((status.BmrLoaded ? "YES" : "NO"))));
        ImGui.SameLine();
        ImGui.TextUnformatted(Ui.T("Ready: {0}", Ui.T((status.ReflectionReady ? "YES" : "NO"))));

        var name = string.IsNullOrWhiteSpace(status.BmrName) ? Ui.T("(not found)") : status.BmrName;
        var internalName = string.IsNullOrWhiteSpace(status.BmrInternalName) ? "-" : status.BmrInternalName;
        ImGui.TextUnformatted(Ui.T("Plugin: {0} / {1} / {2}", name, internalName, status.BmrVersion ?? "-"));
        ImGui.TextWrapped(Ui.T("Reflection state: {0}", Ui.Display(status.ReflectionState)));
        if (!string.IsNullOrWhiteSpace(status.Error))
            ImGui.TextWrapped(Ui.T("Error: {0}", status.Error));

        ImGui.Spacing();
        ImGui.TextUnformatted(Ui.T("Queen disabled: desired={0} actual={1}", Ui.T((status.QueenDesiredDisabled ? "YES" : "NO")), Ui.T((status.QueenActuallyDisabled ? "YES" : "NO"))));
        ImGui.TextUnformatted(Ui.T("Hunts disabled: desired={0} actual={1}", Ui.T((status.HuntsDesiredDisabled ? "YES" : "NO")), Ui.T((status.HuntsActuallyDisabled ? "YES" : "NO"))));
        ImGui.TextUnformatted(Ui.T("Registry: disabled={0} huntsKnown={1} total={2}", status.DisabledRegistryEntryCount, status.KnownHuntModuleCount, status.RegisteredModuleCount));
        ImGui.TextUnformatted(Ui.T("Live removals last update: {0}", status.RemovedLiveModuleCount));

        var currentMax = status.CurrentMaxLoadDistance?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-";
        var capturedMax = status.CapturedMaxLoadDistance?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-";
        ImGui.TextUnformatted(Ui.T("MaxLoadDistance: current={0} minimized={1} capturedReset={2}", currentMax, status.MinimizedMaxLoadDistance.ToString("0.###", CultureInfo.InvariantCulture), capturedMax));
        ImGui.TextWrapped(Ui.T("Last action: {0}", Ui.Display(status.LastAction)));
    }

    private void DrawControls(BmrReflectionStatus status)
    {
        var enabled = plugin.Configuration.ReflectionToolsEnabled;
        if (WindowLayout.Checkbox("Enable BMR reflection tools", ref enabled))
            plugin.BmrReflectionService.SetToolsEnabled(enabled);

        ImGui.Spacing();
        if (ImGui.Button(Ui.L(status.QueenDesiredDisabled ? "Enable Queen Lunatender" : "Disable Queen Lunatender")))
            plugin.BmrReflectionService.RequestQueenLunatenderDisabled(!status.QueenDesiredDisabled);
        ImGui.SameLine();
        ImGui.TextDisabled(Ui.T("OID 0x{0:X}", BmrReflectionService.QueenLunatenderOid));

        if (ImGui.Button(Ui.L(status.HuntsDesiredDisabled ? "Enable Hunt Modules" : "Disable Hunt Modules")))
            plugin.BmrReflectionService.RequestHuntsDisabled(!status.HuntsDesiredDisabled);

        ImGui.Spacing();
        var minimizedValue = plugin.Configuration.ReflectionMinimizedMaxLoadDistance;
        if (ImGui.InputFloat(WindowLayout.InputLabel("Minimized MaxLoadDistance"), ref minimizedValue, 1f, 10f, "%.1f"))
        {
            if (!float.IsFinite(minimizedValue) || minimizedValue <= 0f)
                minimizedValue = BmrReflectionService.DefaultMinimizedMaxLoadDistance;
            plugin.Configuration.ReflectionMinimizedMaxLoadDistance = Math.Clamp(minimizedValue, 0.1f, BmrReflectionService.DefaultFallbackMaxLoadDistance);
            plugin.SaveConfiguration();
        }

        if (ImGui.Button(Ui.L("Minimize MaxLoadDistance")))
            plugin.BmrReflectionService.RequestMinimizeMaxLoadDistance();
        ImGui.SameLine();
        if (ImGui.Button(Ui.L("Reset MaxLoadDistance")))
            plugin.BmrReflectionService.RequestResetMaxLoadDistance();

        ImGui.Spacing();
        if (ImGui.SmallButton(Ui.L("Copy Reflection Status JSON")))
            ImGui.SetClipboardText(plugin.BmrReflectionService.GetStatusJson());
    }
}
