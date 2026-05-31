using System.Numerics;
using ADS.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class LootWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;

    public LootWindow(Plugin plugin)
        : base("ADS Loot###ADSLoot")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360f, 260f),
            MaximumSize = new Vector2(760f, 620f),
        };
        Size = new Vector2(440f, 420f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();

        ImGui.TextUnformatted($"Mode: {plugin.Configuration.LootMode}");
        ImGui.TextWrapped(plugin.LootAutomationService.Status);
        ImGui.Spacing();

        DrawModeButtons();
        ImGui.Separator();
        DrawRegistrableControls();
    }

    private void DrawModeButtons()
    {
        DrawModeButton("Off", LootRollMode.Off);
        ImGui.SameLine();
        DrawModeButton("Need", LootRollMode.Need);
        ImGui.SameLine();
        DrawModeButton("Greed", LootRollMode.Greed);
        ImGui.SameLine();
        DrawModeButton("Pass", LootRollMode.Pass);
    }

    private void DrawModeButton(string label, LootRollMode mode)
    {
        var selected = plugin.Configuration.LootMode == mode;
        if (ImGui.RadioButton(label, selected))
            plugin.SetLootMode(mode);
    }

    private void DrawRegistrableControls()
    {
        var registrableNeed = plugin.Configuration.LootRegistrableNeedingEnabled;
        if (ImGui.Checkbox("Need missing registrables", ref registrableNeed))
            plugin.SetLootRegistrableNeedingEnabled(registrableNeed);

        ImGui.BeginDisabled(!plugin.Configuration.LootRegistrableNeedingEnabled);
        ImGui.Indent(ImGui.GetStyle().IndentSpacing);
        DrawCategoryCheckbox("Mounts", plugin.Configuration.LootRegistrableMountsEnabled, plugin.SetLootRegistrableMountsEnabled);
        DrawCategoryCheckbox("Minions", plugin.Configuration.LootRegistrableMinionsEnabled, plugin.SetLootRegistrableMinionsEnabled);
        DrawCategoryCheckbox("Fashion accessories", plugin.Configuration.LootRegistrableFashionAccessoriesEnabled, plugin.SetLootRegistrableFashionAccessoriesEnabled);
        DrawCategoryCheckbox("Facewear", plugin.Configuration.LootRegistrableFacewearEnabled, plugin.SetLootRegistrableFacewearEnabled);
        DrawCategoryCheckbox("Orchestrion rolls", plugin.Configuration.LootRegistrableOrchestrionRollsEnabled, plugin.SetLootRegistrableOrchestrionRollsEnabled);
        DrawCategoryCheckbox("Faded orchestrion copies", plugin.Configuration.LootRegistrableFadedOrchestrionCopiesEnabled, plugin.SetLootRegistrableFadedOrchestrionCopiesEnabled);
        DrawCategoryCheckbox("Emotes / hairstyles", plugin.Configuration.LootRegistrableEmotesHairstylesEnabled, plugin.SetLootRegistrableEmotesHairstylesEnabled);
        DrawCategoryCheckbox("Bardings", plugin.Configuration.LootRegistrableBardingsEnabled, plugin.SetLootRegistrableBardingsEnabled);
        DrawCategoryCheckbox("Triple Triad cards", plugin.Configuration.LootRegistrableTripleTriadCardsEnabled, plugin.SetLootRegistrableTripleTriadCardsEnabled);
        ImGui.Unindent(ImGui.GetStyle().IndentSpacing);
        ImGui.EndDisabled();
    }

    private static void DrawCategoryCheckbox(string label, bool current, Action<bool> setter)
    {
        var value = current;
        if (ImGui.Checkbox(label, ref value))
            setter(value);
    }
}
