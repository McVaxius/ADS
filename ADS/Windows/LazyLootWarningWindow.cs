using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class LazyLootWarningWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public LazyLootWarningWindow(Plugin plugin)
        : base(
            "LazyLoot Warning###ADSLazyLootWarning",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420f, 0f),
            MaximumSize = new Vector2(520f, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        ImGui.TextWrapped("Are you sure you want to use LazyLoot? It can't recover hidden loot windows and only tries once.");
        ImGui.Spacing();

        if (ImGui.Button("Open /ads loot"))
        {
            plugin.OpenLootUi();
            IsOpen = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Don't show this message again"))
        {
            plugin.Configuration.LazyLootWarningDismissed = true;
            plugin.SaveConfiguration();
            IsOpen = false;
        }
    }
}
